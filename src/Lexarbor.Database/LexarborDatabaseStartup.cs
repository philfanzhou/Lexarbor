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
/// orchestration. Lexarbor keeps the journal-mode normalization ahead of the
/// gate and the executor: EF migrations under the rollback journal.
/// </summary>
/// <remarks>
/// <para>
/// The startup gate is fixed and fails closed. The single-instance mode is
/// authorized first, from declarations alone. The database is then normalized
/// to the rollback journal — see <see cref="NormalizeJournalMode"/> — before
/// the target is observed without touching it: a missing file is created only
/// by an explicit atomic preparation, and a present-but-unusable target is
/// never repaired — with the one exception below. A target conflict on a file
/// that provably exists is the crash-recovery shape: SQLite replays its
/// write-ahead log on one <c>Pooling=false</c> open, one truncating checkpoint
/// folds the log into the database, and the close of that last connection
/// removes the sidecars — a pooled dispose keeps the native handle and the
/// sidecars alive — after which the target is observed exactly once more. Any
/// other unreachable answer, and a recovery that still cannot connect, stop
/// startup without changing the file. Once the normalization runs first, that
/// recovery path stays only for the ServiceMantle observation of a target this
/// build no longer produces; its removal belongs to the shared-entrance work.
/// </para>
/// <para>
/// The migration orchestration runs after the gate: it inspects the EF history
/// through <see cref="LexarborMigrationExecutor"/>, executes only on an empty or
/// pending database, refuses a schema newer than this build, and re-inspects
/// after execution. Failures surface as a fixed diagnostic that carries the
/// ServiceMantle error code only — never a path, connection string, or SQL.
/// </para>
/// </remarks>
public static class LexarborDatabaseStartup
{
    /// <summary>
    /// Bounds the preparation call, the crash-recovery attempt, and waiting on
    /// the orchestration's process-local turn. It bounds neither the EF
    /// migration itself nor total startup.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddLexarborDatabaseStartup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registering the bootstrap provider does not by itself declare a
        // preparation or a deployment capability: each one is registered
        // explicitly, and all three registries share the one provider-id
        // resolver snapshot the bootstrap registry owns — the same shape the
        // ServiceMantle reference service uses.
        services.AddSingleton<IBootstrapDatabaseProvider, SqliteBootstrapDatabaseProvider>();
        services.AddSingleton(provider => new BootstrapDatabaseProviderRegistry(
            provider.GetServices<IBootstrapDatabaseProvider>()));
        services.AddSingleton<SqliteDatabaseTargetPreparationProvider>();
        services.AddSingleton<IDatabaseTargetPreparationProvider>(provider =>
            provider.GetRequiredService<SqliteDatabaseTargetPreparationProvider>());
        services.AddSingleton<IDatabaseDeploymentCapabilityProvider>(provider =>
            provider.GetRequiredService<SqliteDatabaseTargetPreparationProvider>());
        services.AddSingleton(provider => new DatabaseTargetPreparationProviderRegistry(
            provider.GetServices<IDatabaseTargetPreparationProvider>(),
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.AddSingleton(provider => new DatabaseDeploymentCapabilityRegistry(
            provider.GetServices<IDatabaseDeploymentCapabilityProvider>(),
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));

        // No distributed lease is requested or registered: the single-instance
        // orchestration serializes the canonical target inside this process and
        // promises nothing beyond it.
        services.AddSingleton(provider => new DatabaseMigrationLockProviderRegistry(
            providers: null,
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));

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
        // default `data/` directory is created on a first start exactly as the
        // previous initializer did, so a fresh installation (a just-published
        // output directory, an empty volume) starts instead of being rejected
        // as an invalid target. Only the directory is created here; the file
        // itself is still created solely by the atomic preparation below.
        var databasePath = Path.GetFullPath(
            new SqliteConnectionStringBuilder(connectionString).DataSource);
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // The mode decision comes before every side effect, from declarations
        // only: SQLite declares single-instance-only, so this can only detect a
        // registration drift, and it still fails closed.
        var capabilities = provider.GetRequiredService<DatabaseDeploymentCapabilityRegistry>();
        if (!new DatabaseDeploymentValidator(capabilities)
                .Validate(target.Provider, DatabaseDeploymentMode.SingleInstance)
                .IsSupported)
        {
            throw Failed("The SQLite single-instance deployment mode was rejected.");
        }

        var preparation = provider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();
        if (!preparation.TryGetProvider(target.Provider, out var preparationProvider)
            || preparationProvider is null)
        {
            throw Failed("The SQLite target preparation provider is unavailable.");
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

        await ObservePrepareOrRecoverAsync(preparationProvider, target, cancellationToken);

        var orchestrator = new DatabaseMigrationOrchestrator(
            provider.GetRequiredService<IDatabaseMigrationExecutor>(),
            provider.GetRequiredService<DatabaseMigrationLockProviderRegistry>(),
            provider.GetRequiredService<DatabaseDeploymentCapabilityRegistry>());
        var result = await orchestrator.OrchestrateMigrationAsync(
            provider.GetRequiredService<ServiceId>(),
            target,
            DatabaseDeploymentMode.SingleInstance,
            Budget,
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

    private static async Task ObservePrepareOrRecoverAsync(
        IDatabaseTargetPreparationProvider preparationProvider,
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        var observed = await ObserveAsync(preparationProvider, target, cancellationToken);
        switch (observed.Status)
        {
            case DatabaseTargetObservationStatus.TargetConnectable:
                return;

            case DatabaseTargetObservationStatus.TargetMissing:
                // Only here may a file be created, atomically, without
                // replacing anything that appeared in the meantime.
                var prepared = await preparationProvider.PrepareAsync(
                    DatabaseTargetPreparationRequest.ForFile(target),
                    Budget,
                    cancellationToken);
                if (!prepared.Succeeded)
                {
                    throw Failed($"The SQLite database could not be created: {prepared.ErrorCode}.");
                }

                return;

            case DatabaseTargetObservationStatus.TargetUnreachable
                when observed.ErrorCode == WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict
                    && observed.TargetExists == true:
                // Crash recovery: a previous process died with its write-ahead
                // log unreplayed. One open/close lets SQLite replay and
                // checkpoint; a pooled dispose would keep the sidecars alive.
                RecoverSidecars(target.ConnectionString);
                var reobserved = await ObserveAsync(preparationProvider, target, cancellationToken);
                if (reobserved.Status == DatabaseTargetObservationStatus.TargetConnectable)
                {
                    return;
                }

                throw Failed(
                    "The SQLite database still reports a target conflict after one recovery attempt: " +
                    $"{reobserved.ErrorCode ?? observed.ErrorCode}.");

            default:
                // Unreachable for any other reason — including a conflict
                // without a provable file, a failed connection, permissions,
                // and invalid targets such as symbolically linked paths — is
                // never adopted, fixed, or replaced.
                throw Failed(
                    $"The SQLite database target is not usable: {observed.ErrorCode ?? "unknown"}.");
        }
    }

    private static async Task<DatabaseTargetObservation> ObserveAsync(
        IDatabaseTargetPreparationProvider preparationProvider,
        BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        try
        {
            return await preparationProvider.ObserveAsync(target, cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            throw Failed("The SQLite database target could not be observed.");
        }
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

    private static void RecoverSidecars(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var path = Path.GetFullPath(builder.DataSource);
        var recovery = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        };
        try
        {
            using var connection = new SqliteConnection(recovery.ToString());
            connection.Open();
            // The open alone replays the log into memory, but only a
            // checkpoint folds it into the database so that closing the last
            // connection can remove the sidecars — an open/close without the
            // checkpoint leaves them in place and re-observation still fails.
            // A concurrent writer blocks the checkpoint; the pragma then
            // reports busy without throwing, the sidecars stay, and the one
            // re-observation below fails closed.
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteScalar();
            // Closing this connection lets SQLite drop the now-empty sidecar
            // files; a pooled dispose would not release the native handle.
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw Failed("The SQLite crash recovery could not open the database.");
        }
    }

    private static InvalidOperationException Failed(string diagnostic) => new(diagnostic);
}
