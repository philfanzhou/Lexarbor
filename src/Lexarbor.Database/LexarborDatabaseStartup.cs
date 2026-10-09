using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Sqlite;
using ServiceMantle.Migration;

namespace Lexarbor.Database;

/// <summary>
/// Replaces the hand-written database bootstrap with ServiceMantle's SQLite
/// target preparation, single-instance deployment validation, and migration
/// orchestration, consumed through the shared registration and run entrances
/// of the startup gate. Lexarbor keeps the journal-mode normalization ahead of
/// the gate and the executor: EF migrations under the rollback journal.
/// </summary>
/// <remarks>
/// <para>
/// The startup gate is fixed and fails closed. The shared registration
/// entrance provides the provider, preparation, capability and lock registries
/// together with the gate and its receipt; the shared run entrance authorizes
/// the single-instance mode from declarations alone, observes the target
/// without touching it, creates a missing file only through the explicit
/// atomic preparation, never repairs a present-but-unusable one, and then runs
/// the migration orchestration. Lexarbor keeps two steps local, both ahead of
/// the gate: the database directory is created as the previous initializer
/// did, owner-only on Unix so the strict root-key source accepts a fresh
/// <c>data/</c> directory regardless of the process umask, and the database
/// is normalized to the rollback journal —
/// see <see cref="NormalizeJournalMode"/> — so the observation meets neither a
/// WAL header nor a crash-leftover sidecar. The shared SQLite crash-recovery
/// switch stays off: after that normalization nothing this build upgrades can
/// present the shape it exists for. No distributed lease is requested or
/// registered: the single-instance orchestration serializes the canonical
/// target inside this process and promises nothing beyond it.
/// </para>
/// <para>
/// The migration orchestration inspects the EF history through
/// <see cref="LexarborMigrationExecutor"/>, executes only on an empty or
/// pending database, refuses a schema newer than this build, and re-inspects
/// after execution. Failures surface as a fixed diagnostic that carries the
/// ServiceMantle error code only — never a path, connection string, or SQL.
/// </para>
/// </remarks>
public static class LexarborDatabaseStartup
{
    /// <summary>
    /// Bounds the gate's target-preparation call and waiting on the
    /// orchestration's process-local turn. It bounds neither the EF migration
    /// itself nor total startup.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddLexarborDatabaseStartup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The shared registration entrance provides, idempotently, the four
        // registries (bootstrap providers, target preparation, deployment
        // capabilities, migration locks — the last with no lock provider, which
        // is the single-instance shape this product runs), the startup gate,
        // and its receipt. Registering the bootstrap provider does not by
        // itself declare a preparation or a deployment capability: each one is
        // registered explicitly through the provider interfaces, the same
        // shape the ServiceMantle reference service uses.
        services.AddServiceMantleStartupDatabaseGateServices();
        services.AddSingleton<IBootstrapDatabaseProvider, SqliteBootstrapDatabaseProvider>();
        services.AddSingleton<SqliteDatabaseTargetPreparationProvider>();
        services.AddSingleton<IDatabaseTargetPreparationProvider>(provider =>
            provider.GetRequiredService<SqliteDatabaseTargetPreparationProvider>());
        services.AddSingleton<IDatabaseDeploymentCapabilityProvider>(provider =>
            provider.GetRequiredService<SqliteDatabaseTargetPreparationProvider>());

        services.AddScoped<IDatabaseMigrationExecutor, LexarborMigrationExecutor>();
        return services;
    }

    /// <summary>
    /// Runs the startup gate and the migration orchestration once. The caller
    /// owns the readiness outcome: a return means success, and any failure is
    /// thrown as a fixed safe diagnostic for the caller to fail startup with.
    /// </summary>
    public static async Task RunStartupMigrationAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(nameof(LexarborDatabaseStartup));
        var context = provider.GetRequiredService<VocabularyDbContext>();
        var connectionString = context.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw Failed("The SQLite connection string is not configured.");
        }

        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.Sqlite,
            serverVersion: null,
            connectionString);

        // The database directory is part of the deployment contract: the
        // default `data/` directory is created on a first start so a fresh
        // installation (a just-published output directory, an empty volume)
        // starts instead of being rejected as an invalid target. On Unix it
        // is created with exactly the owner-only mode bits, not the process
        // umask: this directory is also the parent of the Data Protection
        // root-key file, whose strict shared source refuses a wider one, so a
        // umask-022 first start would otherwise fail closed on its own fresh
        // directory. Only the directory is created here; the file itself is
        // still created solely by the atomic preparation below. A directory
        // that already exists — including one a deployment deliberately left
        // wider — is never re-chmod; the root-key source keeps refusing that
        // shape.
        var databasePath = Path.GetFullPath(
            new SqliteConnectionStringBuilder(connectionString).DataSource);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // The rollback-journal normalization runs before the gate observes the
        // target. A database still carrying the WAL header — an existing
        // deployment of the releases that enabled it, or one restored from an
        // old backup — is converted here, replaying any committed frames of a
        // leftover write-ahead log into the file first. The placement before
        // the observation is what keeps a 0.3.1-style header precheck, which
        // refuses a clean WAL target on sight, able to observe the databases
        // this build upgrades, and it survives the observation moving to a
        // shared preparation entrance.
        NormalizeJournalMode(databasePath, connectionString);

        // One shared run entrance covers the whole fixed sequence: the
        // single-instance mode is authorized from declarations alone (SQLite
        // declares single-instance-only, so this can only detect a
        // registration drift, and it still fails closed), the target is
        // observed and — only when missing — atomically created, and the EF
        // migration orchestration runs through the shared gate over the
        // executor registered above. The shared SQLite recovery switch stays
        // off: the normalization ahead of the gate has already folded away
        // every shape it exists for.
        var gateOptions = new StartupDatabaseGateOptions(
            target,
            DatabaseDeploymentMode.SingleInstance,
            Budget,
            enableTargetPreparation: true,
            allowTargetCreation: true,
            maintenanceConnectionString: null,
            preparationTimeout: Budget);
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
        // path, no connection string, and no orchestrator message.
        logger.LogInformation(
            "Database startup migration finished; executor ran: {ExecutorWasCalled}.",
            result.ExecutorWasCalled);
    }

    /// <summary>
    /// Normalizes an existing database to the rollback journal, the one journal
    /// mode this product runs. On a database still carrying the WAL header the
    /// single <c>Pooling=false</c> open lets SQLite replay any committed frames
    /// of a leftover <c>-wal</c> file, and the pragma performs the one
    /// checkpoint that folds them into the database, switches the header, and
    /// drops the sidecars when that last connection closes. On a
    /// rollback-journal database the step is a no-op except for one probe
    /// read, which replays a crash-leftover hot <c>-journal</c> back natively.
    /// A missing file is skipped — only the gate's atomic preparation creates
    /// it, already in the rollback mode. Any failure fails closed through
    /// <see cref="Failed"/> without changing the file.
    /// </summary>
    private static void NormalizeJournalMode(string databasePath, string connectionString)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        // The write-ahead mode used to fail a read-only mount implicitly —
        // opening a WAL database there cannot create its shared-memory file.
        // The rollback journal has no such artifact, so the same shape is
        // refused explicitly: the normalization must be able to open the
        // database for writing (the conversion itself, the executor's
        // migrations, and every write request need it), and a read-only file
        // or mount answers here instead of the service serving reads forever.
        try
        {
            using var writability = new FileStream(
                databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failed("The SQLite database could not be opened for writing.");
        }

        var normalization = new SqliteConnectionStringBuilder(connectionString)
        {
            DataSource = databasePath,
            Pooling = false,
            // ReadWrite, not the default ReadWriteCreate: this step never
            // creates the file — a target that vanished between the check and
            // the open is a failure for the caller to classify, and file
            // creation stays the gate's alone.
            Mode = SqliteOpenMode.ReadWrite
        };
        string? mode;
        try
        {
            using var connection = new SqliteConnection(normalization.ToString());
            connection.Open();
            // One lightweight read first: a rollback crash leaves a hot
            // -journal file, and SQLite replays it back only when a statement
            // reads a page — the journal-mode pragma alone is a no-op on an
            // already-rollback database and would leave the journal behind for
            // the gate to observe as a conflict.
            using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master;";
            probe.ExecuteScalar();
            using var command = connection.CreateCommand();
            // PRAGMA cannot be composed into a query, so it goes through the
            // raw command. A conversion blocked by a concurrent holder answers
            // with the unchanged mode instead of throwing, so the returned
            // mode is the success proof.
            command.CommandText = "PRAGMA journal_mode=DELETE;";
            mode = command.ExecuteScalar()?.ToString();
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw Failed("The SQLite journal-mode normalization could not open the database.");
        }

        // A replayed hot journal can leave a zeroed -journal file behind: its
        // content is already folded back into the database, but the gate's
        // strict observation reads any leftover journal file as a conflict
        // shape. Deleting is safe only because the probe read above succeeded
        // — a journal that still needed replaying would have replayed there,
        // or the step would already have failed closed.
        try
        {
            File.Delete(databasePath + "-journal");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failed("The SQLite journal-mode normalization could not remove a leftover journal file.");
        }

        if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
        {
            throw Failed("The SQLite journal mode could not be normalized.");
        }
    }

    private static InvalidOperationException Failed(string diagnostic) => new(diagnostic);
}
