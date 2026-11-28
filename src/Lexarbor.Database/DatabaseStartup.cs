using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.PostgreSql;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Migration;

namespace Lexarbor.Database;

/// <summary>
/// Runs the database startup through ServiceMantle's PostgreSQL bootstrap
/// provider, target preparation, deployment capability, and advisory
/// migration lock, consumed through the shared registration and run entrances
/// of the startup gate.
/// </summary>
/// <remarks>
/// <para>
/// The startup gate is fixed and fails closed. The shared registration
/// entrance provides the provider, preparation, capability and lock registries
/// together with the gate and its receipt; this class registers the PostgreSQL
/// implementation of each. The shared run entrance observes the target,
/// creates a missing database only through the explicit preparation (with
/// the target login as owner), never repairs a present-but-unusable one, and
/// then runs the migration orchestration under a session-level advisory lock
/// derived from the service id, so two processes starting against the same
/// database never migrate it at the same time.
/// </para>
/// <para>
/// The advisory lock is the gate's <c>MultiInstance</c> orchestration. It
/// coordinates startup migrations. Lexarbor supports one serving instance.
/// </para>
/// <para>
/// The migration orchestration inspects the EF history through
/// <see cref="MigrationExecutor"/>, executes only on an empty or
/// pending database, refuses a schema newer than this build, and re-inspects
/// after execution. Failures surface as a fixed diagnostic that carries the
/// ServiceMantle error code only — never a host, connection string, or SQL.
/// </para>
/// </remarks>
public static class DatabaseStartup
{
    public static IServiceCollection AddDatabaseStartup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The shared registration entrance provides, idempotently, the four
        // registries (bootstrap providers, target preparation, deployment
        // capabilities, migration locks), the startup gate, and its receipt.
        // Each PostgreSQL implementation is registered explicitly, the same
        // shape the ServiceMantle reference service uses.
        services.AddServiceMantleStartupDatabaseGateServices();
        services.AddSingleton<IBootstrapDatabaseProvider, PostgreSqlBootstrapDatabaseProvider>();
        services.AddSingleton<IDatabaseTargetPreparationProvider, PostgreSqlDatabaseTargetPreparationProvider>();
        services.AddServiceMantlePostgreSqlDeploymentCapability();
        services.AddSingleton<IDatabaseMigrationLockProvider, PostgreSqlMigrationLockProvider>();

        services.AddScoped<IDatabaseMigrationExecutor, MigrationExecutor>();
        return services;
    }

    /// <summary>
    /// Runs the startup gate and the migration orchestration once. The caller
    /// owns the readiness outcome: a return means success, and any failure is
    /// thrown as a fixed safe diagnostic for the caller to fail startup with.
    /// </summary>
    public static async Task RunMigrationAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(DatabaseStartup));
        var context = provider.GetRequiredService<VocabularyDbContext>();
        var connectionString = context.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw Failed("The PostgreSQL connection string is not configured.");
        }

        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            serverVersion: null,
            connectionString);

        // The ServiceMantle preset: multi-instance migration coordination
        // through the advisory lock, target preparation enabled, creation of a
        // missing database permitted through the derived maintenance
        // connection (`postgres`), and thirty-second lock and preparation
        // budgets. An operator-created database is only observed.
        StartupDatabaseGateOptions gateOptions;
        try
        {
            gateOptions = PostgreSqlStartupDatabaseGateOptions.Create(target, allowTargetCreation: true);
        }
        catch (ArgumentException)
        {
            // The preset's own message is fixed and coded, but the startup
            // diagnostic stays Lexarbor's: no parser detail crosses it.
            throw Failed("The PostgreSQL connection string is invalid.");
        }

        var result = await provider.GetRequiredService<StartupDatabaseGate>()
            .RunAsync(
                gateOptions,
                provider.GetRequiredService<StartupDatabaseReceipt>(),
                provider.GetRequiredService<ServiceId>(),
                cancellationToken);

        if (!result.Succeeded)
        {
            throw Failed($"The database startup migration was refused: {result.ErrorCode}.");
        }

        // The one success line carries the finite outcome and nothing else: no
        // host, no connection string, and no orchestrator message.
        logger.LogInformation(
            "Database startup migration finished; executor ran: {ExecutorWasCalled}.",
            result.ExecutorWasCalled);
    }

    private static InvalidOperationException Failed(string diagnostic) => new(diagnostic);
}
