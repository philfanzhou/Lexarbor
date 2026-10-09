using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Migration;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Pins the ServiceMantle startup gate that replaced the hand-written
/// <c>DatabaseInitializer</c>, including the rollback-journal normalization
/// that runs before the gate observes the target: first start creates and
/// migrates through the target-preparation flow already in the rollback
/// journal, later starts skip, a pending migration executes and re-inspects, a
/// newer schema and a failing migration stop startup with fixed safe
/// diagnostics, a database still carrying the WAL header — clean or with a
/// crash-leftover <c>-wal</c> sidecar — is converted with committed data
/// intact, a rollback crash's <c>-journal</c> file is rolled back, and every
/// refusal leaves the file bytes untouched without echoing a path. Every
/// database goes through the real host startup, so these tests use real files
/// under the gate-safe directory — never <c>:memory:</c> and never a path that
/// resolves through a symbolic link, both of which the strict gate rejects.
/// </summary>
public class DatabaseStartupGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string CreateGateDirectory(string name) =>
        VocabularyWebApplicationFactory.CreateGateSafeDirectory($"lexarbor-gate-{name}-{Guid.NewGuid():N}");

    /// <summary>
    /// Reads the SQLite header's file-format version bytes (offsets 18 and 19):
    /// 1/1 is the rollback journal, 2/2 write-ahead logging. This is the
    /// persisted fact the acceptance rows speak of, independent of any probe
    /// connection's own mode.
    /// </summary>
    private static (byte Write, byte Read) ReadHeaderJournalBytes(string database)
    {
        using var file = File.OpenRead(database);
        var buffer = new byte[2];
        file.Position = 18;
        Assert.Equal(2, file.Read(buffer, 0, 2));
        return (buffer[0], buffer[1]);
    }

    private static async Task<string> ReadJournalModeAsync(string database)
    {
        await using var probe = new SqliteConnection($"Data Source={database};Pooling=False");
        await probe.OpenAsync(Ct);
        using var command = probe.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return command.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Puts a migrated database back into the WAL shape of the releases this
    /// build upgrades: the header switches to 2/2 and the last close
    /// checkpoints the sidecars away, leaving a clean WAL database.
    /// </summary>
    private static async Task SwitchToWalAsync(string database)
    {
        await using var probe = new SqliteConnection($"Data Source={database};Pooling=False");
        await probe.OpenAsync(Ct);
        using var command = probe.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        Assert.Equal("wal", command.ExecuteScalar()?.ToString());
    }

    /// <summary>
    /// A shared key root inside the gate directory: every host a test starts
    /// on one database file must decrypt that file's ServiceMantle key rows
    /// with the same root key, or the startup key-ring probe fails closed.
    /// </summary>
    private static string CreateSharedKeyRoot(string directory)
    {
        var keyRoot = Path.Combine(directory, "keys");
        Directory.CreateDirectory(keyRoot);
        return keyRoot;
    }

    private static VocabularyWebApplicationFactory CreateHost(
        string databasePath,
        string? keyContentRoot = null) =>
        new("Testing", includeAppCredentials: true, keyContentRoot: keyContentRoot, databasePath: databasePath);

    [Fact]
    public async Task FirstStart_InEmptyDirectory_CreatesMigratesInRollbackJournalMode()
    {
        var directory = CreateGateDirectory("first-start");
        var database = Path.Combine(directory, "vocabulary.db");
        try
        {
            Assert.False(File.Exists(database));

            await using var factory = CreateHost(database);
            using var client = factory.CreateClient();

            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            Assert.True(File.Exists(database));
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Equal(
                context.Database.GetMigrations().Order(),
                (await context.Database.GetAppliedMigrationsAsync(Ct)).Order());

            // The rollback journal is the created shape itself: the header
            // carries 1/1 and a connection that did nothing to set it still
            // reads delete. The normalization never ran — the file did not
            // exist — and the gate's atomic preparation created it this way.
            Assert.Equal((1, 1), ReadHeaderJournalBytes(database));
            Assert.Equal("delete", await ReadJournalModeAsync(database));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task FirstStart_WithoutTheParentDirectory_CreatesItAndMigrates()
    {
        // A fresh installation — a just-published output directory or an
        // empty volume — has no data directory at all yet. The gate creates
        // it like the previous initializer did instead of rejecting the
        // target, while the file itself is still created only by the atomic
        // preparation.
        var root = CreateGateDirectory("missing-parent");
        var nested = Path.Combine(root, "data", "nested");
        var database = Path.Combine(nested, "vocabulary.db");
        try
        {
            Assert.False(Directory.Exists(nested));

            await using var factory = CreateHost(database);
            using var client = factory.CreateClient();

            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            Assert.True(File.Exists(database));
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Equal(
                context.Database.GetMigrations().Order(),
                (await context.Database.GetAppliedMigrationsAsync(Ct)).Order());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SecondStart_OnMigratedDatabase_SkipsTheExecutorAndKeepsData()
    {
        var directory = CreateGateDirectory("second-start");
        var database = Path.Combine(directory, "vocabulary.db");
        var keyRoot = CreateSharedKeyRoot(directory);
        try
        {
            await using (var first = CreateHost(database, keyRoot))
            {
                using var client = first.CreateClient();
                await using var scope = first.Services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
                var now = DateTimeOffset.UtcNow;
                context.Vocabularies.Add(new VocabularyEntity
                {
                    Id = Guid.NewGuid().ToString(),
                    Word = "apple",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await context.SaveChangesAsync(Ct);
            }

            var executions = 0;
            await using var second = CreateHost(database, keyRoot).WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDatabaseMigrationExecutor>();
                    services.AddScoped<IDatabaseMigrationExecutor>(provider =>
                    {
                        executions = 0;
                        return new CountingMigrationExecutor(
                            new LexarborMigrationExecutor(
                                provider.GetRequiredService<VocabularyDbContext>()),
                            () => executions++);
                    });
                });
            });
            using var client2 = second.CreateClient();

            using var ready = await client2.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            Assert.Equal(0, executions);
            await using var scope2 = second.Services.CreateAsyncScope();
            var context2 = scope2.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Equal(["apple"], await context2.Vocabularies.Select(item => item.Word).ToListAsync(Ct));

            // A rollback-journal database crosses the normalization as a
            // no-op: header 1/1 stays 1/1, the mode still reads delete, and
            // the gate observed the target as connectable.
            Assert.Equal((1, 1), ReadHeaderJournalBytes(database));
            Assert.Equal("delete", await ReadJournalModeAsync(database));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task PendingMigration_IsAppliedAndReinspected()
    {
        var directory = CreateGateDirectory("pending");
        var database = Path.Combine(directory, "vocabulary.db");
        var keyRoot = CreateSharedKeyRoot(directory);
        try
        {
            string lastMigration;
            await using (var first = CreateHost(database, keyRoot))
            {
                using var client = first.CreateClient();
                await using var scope = first.Services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
                lastMigration = context.Database.GetMigrations().Order().Last();
            }

            // Rewind to a pending state: the newest migration's table is gone
            // and its history row with it, so the re-run has real work to do.
            var lastMigrationTable = lastMigration.EndsWith("AddServiceAuditLogs", StringComparison.Ordinal)
                ? "service_audit_logs"
                : "service_data_protection_keys";
            await using (var raw = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await raw.OpenAsync(Ct);
                await ExecuteAsync(raw, $"DROP TABLE \"{lastMigrationTable}\";", Ct);
                await ExecuteAsync(
                    raw,
                    $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{lastMigration}';",
                    Ct);
            }

            var executions = 0;
            await using var second = CreateHost(database, keyRoot).WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDatabaseMigrationExecutor>();
                    services.AddScoped<IDatabaseMigrationExecutor>(provider =>
                    {
                        executions = 0;
                        return new CountingMigrationExecutor(
                            new LexarborMigrationExecutor(
                                provider.GetRequiredService<VocabularyDbContext>()),
                            () => executions++);
                    });
                });
            });
            using var client2 = second.CreateClient();

            using var ready = await client2.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            Assert.Equal(1, executions);
            await using var scope2 = second.Services.CreateAsyncScope();
            var context2 = scope2.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Equal(
                context2.Database.GetMigrations().Order(),
                (await context2.Database.GetAppliedMigrationsAsync(Ct)).Order());
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task NewerSchemaVersion_RefusesStartupWithoutTouchingTheDatabase()
    {
        var directory = CreateGateDirectory("too-new");
        var database = Path.Combine(directory, "vocabulary.db");
        try
        {
            await using (var first = CreateHost(database))
            {
                using var client = first.CreateClient();
            }

            const string futureMigration = "99991231235959_FromTheFuture";
            await using (var raw = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await raw.OpenAsync(Ct);
                await ExecuteAsync(
                    raw,
                    $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                    $"VALUES ('{futureMigration}', '10.0.0');",
                    Ct);
            }

            await using var second = CreateHost(database);
            var failure = Assert.Throws<InvalidOperationException>(() => second.CreateClient());

            Assert.Contains("migration.version_too_new", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(database, failure.Message, StringComparison.Ordinal);

            // A newer database is refused, not rewritten.
            await using var probe = new SqliteConnection($"Data Source={database};Pooling=False");
            await probe.OpenAsync(Ct);
            using var command = probe.CreateCommand();
            command.CommandText =
                "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '" + futureMigration + "'";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task FailingMigration_StopsStartupWithSafeDiagnosticAndKeepsTheFile()
    {
        var directory = CreateGateDirectory("failing");
        var database = Path.Combine(directory, "vocabulary.db");
        try
        {
            // A schema without history inspects as empty, so the executor runs
            // and its migration fails on the pre-existing table.
            await using (var raw = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await raw.OpenAsync(Ct);
                await ExecuteAsync(raw, "CREATE TABLE \"vocabulary\" (\"id\" TEXT NOT NULL);", Ct);
            }

            await using var factory = CreateHost(database);
            var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

            Assert.Contains("migration.execution_failed", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(database, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("CREATE TABLE", failure.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(database));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task WalDatabase_OnNextStart_IsConvertedToRollbackJournalWithDataIntact()
    {
        var directory = CreateGateDirectory("wal-conversion");
        var database = Path.Combine(directory, "vocabulary.db");
        var keyRoot = CreateSharedKeyRoot(directory);
        try
        {
            await using (var first = CreateHost(database, keyRoot))
            {
                using var client = first.CreateClient();
                await using var scope = first.Services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
                var now = DateTimeOffset.UtcNow;
                context.Vocabularies.Add(new VocabularyEntity
                {
                    Id = Guid.NewGuid().ToString(),
                    Word = "apple",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await context.SaveChangesAsync(Ct);
            }

            // The shape this build upgrades: an existing deployment of the WAL
            // releases — a clean 2/2 header, no sidecars left by the last
            // close.
            await SwitchToWalAsync(database);
            Assert.Equal((2, 2), ReadHeaderJournalBytes(database));
            Assert.False(File.Exists(database + "-wal"));

            await using var second = CreateHost(database, keyRoot);
            using var client2 = second.CreateClient();

            using var ready = await client2.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            // The conversion happened before the gate observed the target:
            // header 1/1, no sidecars, and every committed row intact.
            Assert.Equal((1, 1), ReadHeaderJournalBytes(database));
            Assert.Equal("delete", await ReadJournalModeAsync(database));
            Assert.False(File.Exists(database + "-wal"));
            Assert.False(File.Exists(database + "-shm"));
            await using var scope2 = second.Services.CreateAsyncScope();
            var context2 = scope2.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Equal(["apple"], await context2.Vocabularies.Select(item => item.Word).ToListAsync(Ct));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ReadonlyDatabaseFile_FailsStartupWithoutServing()
    {
        // The write-ahead deployments refused a read-only target implicitly:
        // opening a WAL database there cannot create its shared-memory file.
        // The rollback journal has no such artifact, so the refusal is kept
        // as an explicit writability check ahead of the gate — read-only
        // storage still never serves.
        var directory = CreateGateDirectory("readonly");
        var database = Path.Combine(directory, "vocabulary.db");
        try
        {
            await using (var first = CreateHost(database))
            {
                using var client = first.CreateClient();
            }

            File.SetUnixFileMode(
                database,
                File.GetUnixFileMode(database) & ~UnixFileMode.UserWrite
                    & ~UnixFileMode.GroupWrite & ~UnixFileMode.OtherWrite);

            await using var second = CreateHost(database);
            var failure = Assert.Throws<InvalidOperationException>(() => second.CreateClient());

            Assert.Contains(
                "The SQLite database could not be opened for writing.",
                failure.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(database, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (File.Exists(database))
                {
                    File.SetUnixFileMode(
                        database,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (IOException)
            {
                // The deletion below is best effort either way.
            }

            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CrashLeftoverSidecars_AreRecoveredWithCommittedDataIntact()
    {
        var directory = CreateGateDirectory("crash-recovery");
        var database = Path.Combine(directory, "vocabulary.db");
        var keyRoot = CreateSharedKeyRoot(directory);
        try
        {
            await using (var first = CreateHost(database, keyRoot))
            {
                using var client = first.CreateClient();
            }

            // A real crash, not a simulation of its file state: the child
            // commits through a write-ahead connection and is then SIGKILLed,
            // so the commit survives only in the -wal sidecar with no live
            // holder — a clean close would have checkpointed it away. The
            // database is switched back to WAL first, because this build
            // otherwise never produces the sidecar shape.
            await SwitchToWalAsync(database);
            var marker = Path.Combine(directory, "committed.marker");
            using var child = await StartCrashSimulatorAsync(database, marker, Ct);
            try
            {
                Assert.True(File.Exists(database + "-wal"));

                child.Kill(entireProcessTree: true);
                Assert.True(child.WaitForExit(10_000));
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }

            Assert.True(File.Exists(database + "-wal"));

            await using var second = CreateHost(database, keyRoot);
            using var client2 = second.CreateClient();

            using var ready = await client2.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            // The pre-gate normalization replays the log, folds it into the
            // database, switches the header, and drops the sidecars — the
            // committed transaction survives into the rollback-journal file.
            await using var scope = second.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Equal(
                ["crash-survivor"],
                await context.Vocabularies.Select(item => item.Word).ToListAsync(Ct));
            Assert.Equal((1, 1), ReadHeaderJournalBytes(database));
            Assert.False(File.Exists(database + "-wal"));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task RollbackCrashHotJournal_IsRolledBackOnStartup()
    {
        var directory = CreateGateDirectory("hot-journal");
        var database = Path.Combine(directory, "vocabulary.db");
        var keyRoot = CreateSharedKeyRoot(directory);
        try
        {
            await using (var first = CreateHost(database, keyRoot))
            {
                using var client = first.CreateClient();
            }

            // A rollback crash — the normal crash shape once the rollback
            // journal is the only mode: an IMMEDIATE transaction wrote a row,
            // was killed before committing, and left the hot -journal file
            // holding the original page images.
            var marker = Path.Combine(directory, "uncommitted.marker");
            using var child = await StartCrashSimulatorAsync(database, marker, Ct, "hot-journal");
            try
            {
                Assert.True(File.Exists(database + "-journal"));

                child.Kill(entireProcessTree: true);
                Assert.True(child.WaitForExit(10_000));
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }

            Assert.True(File.Exists(database + "-journal"));

            await using var second = CreateHost(database, keyRoot);
            using var client2 = second.CreateClient();

            using var ready = await client2.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            // The opening connection rolls the hot journal back natively: the
            // uncommitted row is gone, the journal file is replayed away, and
            // the database stays in the rollback mode.
            await using var scope2 = second.Services.CreateAsyncScope();
            var context2 = scope2.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Empty(await context2.Vocabularies.Select(item => item.Word).ToListAsync(Ct));
            Assert.False(File.Exists(database + "-journal"));
            Assert.Equal((1, 1), ReadHeaderJournalBytes(database));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SidecarsWithoutMainFile_RefuseStartupWithoutCreatingTheFile()
    {
        var directory = CreateGateDirectory("orphan-sidecars");
        var database = Path.Combine(directory, "vocabulary.db");
        try
        {
            await using (var first = CreateHost(database))
            {
                using var client = first.CreateClient();
            }

            // The sidecar shape is prepared by switching the database back to
            // WAL first — this build otherwise never produces sidecars.
            await SwitchToWalAsync(database);
            var marker = Path.Combine(directory, "committed.marker");
            using var child = await StartCrashSimulatorAsync(database, marker, Ct);
            try
            {
                Assert.True(File.Exists(database + "-wal"));

                child.Kill(entireProcessTree: true);
                Assert.True(child.WaitForExit(10_000));
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }

            File.Delete(database);
            Assert.True(File.Exists(database + "-wal"));

            // Sidecars without a main file are a conflict that is not a crash
            // to recover: nothing is created, adopted, or repaired.
            await using var second = CreateHost(database);
            var failure = Assert.Throws<InvalidOperationException>(() => second.CreateClient());

            Assert.Contains("database_target_preparation.target_conflict", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(database, failure.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(database));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void PlaceholderFile_IsRefusedWithBytesUnchanged()
    {
        var directory = CreateGateDirectory("placeholder");
        var database = Path.Combine(directory, "vocabulary.db");
        try
        {
            var placeholder = "this file is not a database"u8.ToArray();
            File.WriteAllBytes(database, placeholder);

            using var factory = CreateHost(database);
            var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

            // The pre-gate journal-mode normalization opens an existing file
            // before the gate does, so a file that is not a database stops
            // startup at that step with the normalization's fixed safe
            // diagnostic — still no path, and still byte-for-byte unchanged.
            Assert.Contains(
                "The SQLite journal-mode normalization could not open the database.",
                failure.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain(database, failure.Message, StringComparison.Ordinal);
            Assert.Equal(placeholder, File.ReadAllBytes(database));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DisabledInitialization_KeepsHostRunningNotReadyAndFileUntouched()
    {
        var directory = CreateGateDirectory("disabled");
        var database = Path.Combine(directory, "vocabulary.db");
        try
        {
            var placeholder = "pre-mounted placeholder"u8.ToArray();
            File.WriteAllBytes(database, placeholder);

            await using var factory = new VocabularyWebApplicationFactory(
                "Testing",
                includeAppCredentials: true,
                extraConfiguration: new Dictionary<string, string?>
                {
                    ["Database:InitializeOnStartup"] = "false"
                },
                databasePath: database);
            using var client = factory.CreateClient();

            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct));
            Assert.Equal("not_ready", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("notStarted", body.RootElement.GetProperty("migrationStatus").GetString());
            Assert.Equal("unreachable", body.RootElement.GetProperty("databaseStatus").GetString());
            Assert.Equal(placeholder, File.ReadAllBytes(database));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task DefaultTimeoutConnectionString_PassesTheGate()
    {
        var directory = CreateGateDirectory("default-timeout");
        var database = Path.Combine(directory, "vocabulary.db");
        try
        {
            await using var factory = new VocabularyWebApplicationFactory(
                "Testing",
                includeAppCredentials: true,
                extraConfiguration: new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = $"Data Source={database};Pooling=False;Default Timeout=5"
                },
                databasePath: database);
            using var client = factory.CreateClient();

            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.True(File.Exists(database));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SymbolicLinkTarget_IsRefusedWithoutEchoingThePath()
    {
        var directory = CreateGateDirectory("symlink");
        var database = Path.Combine(directory, "vocabulary.db");
        var linked = Path.Combine(directory, "linked.db");
        try
        {
            await using (var first = CreateHost(database))
            {
                using var client = first.CreateClient();
            }

            File.CreateSymbolicLink(linked, database);
            Assert.True(File.Exists(linked));

            using var factory = CreateHost(linked);
            var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

            Assert.Contains("database_target_preparation.invalid_target", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(linked, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(database, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Spawns the crash-simulator child on the simulator assembly built with
    /// the solution, waits until its row is written (the marker is written
    /// strictly after the INSERT), and returns the live process for the caller
    /// to SIGKILL. The mode selects the crash shape: <c>wal</c> (default)
    /// commits into the write-ahead log, <c>hot-journal</c> leaves the row
    /// uncommitted inside an IMMEDIATE transaction.
    /// </summary>
    private static async Task<Process> StartCrashSimulatorAsync(
        string databasePath,
        string markerPath,
        CancellationToken cancellationToken,
        string mode = "wal")
    {
        var simulator = ResolveSimulatorAssembly();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(simulator);
        startInfo.ArgumentList.Add(databasePath);
        startInfo.ArgumentList.Add(markerPath);
        startInfo.ArgumentList.Add(mode);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The crash simulator process could not be started.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        while (!File.Exists(markerPath))
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The crash simulator exited early with code {process.ExitCode}.");
            }

            await Task.Delay(50, timeout.Token);
        }

        return process;
    }

    private static string ResolveSimulatorAssembly()
    {
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var candidate = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "Lexarbor.SqliteCrashSimulator", "bin", configuration, "net10.0",
                "Lexarbor.SqliteCrashSimulator.dll"));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "The Lexarbor.SqliteCrashSimulator assembly was not found; build the solution before running the gate tests.");
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A WAL sidecar that SQLite still holds goes with the next run's
            // new directory name.
        }
    }

    /// <summary>
    /// Wraps the real executor so a host under test can count the executions
    /// the ServiceMantle orchestration decided to run, while every inspection
    /// and execution still runs the real EF logic.
    /// </summary>
    private sealed class CountingMigrationExecutor(
        IDatabaseMigrationExecutor inner,
        Action onExecuted) : IDatabaseMigrationExecutor
    {
        public ValueTask<MigrationObservationState> InspectAsync(
            CancellationToken cancellationToken = default) =>
            inner.InspectAsync(cancellationToken);

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            onExecuted();
            return inner.ExecuteAsync(cancellationToken);
        }
    }
}
