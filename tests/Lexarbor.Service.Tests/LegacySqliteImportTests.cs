using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.LegacySqlite;
using Lexarbor.Domain.Models;
using Lexarbor.Service.Tests.TestInfrastructure;
using Lexarbor.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Pins ADR-011's transition release through the real host startup: a SQLite
/// database of an earlier release is copied, verified and recorded in one
/// PostgreSQL transaction before the service is ready; the source file is
/// never modified; an older SQLite schema is upgraded on a private working
/// copy; ASCII-only normalization, byte-wise identity and insertion order
/// survive; and every refusal or failure leaves PostgreSQL without copied rows
/// or a marker, so the next start can try again.
/// </summary>
public class LegacySqliteImportTests
{
    private const string MeaningSubmissionsMigration = "20261031172216_AddMeaningSubmissions";
    private const string Created = "2026-03-04 05:06:07.1234567+08:00";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstStart_CopiesVerifiesAndRecords_LeavingTheSourceUnchanged()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        using var data = new DataDirectory();
        await CreateSourceAsync(data.Source, seed: SeedLatestAsync);
        var before = Hash(data.Source);

        using var logs = new RecordingLogs();
        await using (var host = CreateHost(database.ConnectionString, data.Root))
        {
            using var client = Logged(host, logs).CreateClient();
            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        Assert.Equal(before, Hash(data.Source));
        AssertOnlyTheSourceRemains(data);
        Assert.Contains(logs.Entries, entry => entry.StartsWith("SQLite import finished: ", StringComparison.Ordinal));

        await using var context = database.Open();
        var marker = await context.LegacySqliteImports.SingleAsync(Ct);
        Assert.Equal(LegacySqliteImportEntity.Imported, marker.Outcome);
        Assert.Equal(LegacySqliteMigrationContext.LastMigration, marker.SourceSchemaVersion);
        Assert.Equal(34, marker.CopiedTables);
        Assert.Equal(9, marker.CopiedRows);

        // ASCII-only folding: SQLite's lower() kept É, so the two spellings stay distinct keys.
        var words = await context.Vocabularies.AsNoTracking()
            .OrderBy(word => EF.Property<long>(word, VocabularyDbContext.InsertionOrderProperty))
            .Select(word => new { word.Id, word.Word, word.NormalizedWord, word.CreatedAt })
            .ToListAsync(Ct);
        Assert.Equal(["w-zebra", "w-cafe-upper", "w-cafe", "w-apple"], words.Select(word => word.Id));
        Assert.Equal("cafÉ", words[1].NormalizedWord);
        Assert.Equal("café", words[2].NormalizedWord);
        // Truncated to PostgreSQL's microseconds and stored as the same instant.
        Assert.Equal(DateTimeOffset.Parse("2026-03-03T21:06:07.123456Z"), words[0].CreatedAt);

        var meaning = await context.VocabularyMeanings.AsNoTracking().SingleAsync(item => item.Id == "m-cafe-upper", Ct);
        Assert.Equal("n.", meaning.NormalizedPartOfSpeech);
        Assert.Equal("coffee house", meaning.NormalizedMeaning);

        var key = await context.BusinessApiKeys.AsNoTracking().SingleAsync(Ct);
        Assert.True(key.Catalog);
        Assert.False(key.MeaningSubmissions);
        Assert.Null(key.RevokedAtUnixMs);

        await using var connection = await database.OpenConnectionAsync();
        await using (var audit = new NpgsqlCommand("SELECT occurred_at_utc, outcome FROM service_audit_logs", connection))
        await using (var reader = await audit.ExecuteReaderAsync(Ct))
        {
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal(new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc).AddTicks(1234560), reader.GetFieldValue<DateTime>(0));
            Assert.Equal(1, reader.GetInt32(1));
        }

        // The identity continues after the copied rowids, and the triggers are back.
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO vocabulary (id, word, created_at, updated_at) VALUES ('w-new', 'new', now(), now()) RETURNING insertion_order",
            connection))
        {
            Assert.Equal(5L, await insert.ExecuteScalarAsync(Ct));
        }

        await using var triggers = new NpgsqlCommand(
            "SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal AND tgenabled <> 'O'", connection);
        Assert.Equal(0L, await triggers.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task FirstStart_PreservesAudioUploadAndBindingEnums()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        using var data = new DataDirectory();
        var hash = new string('a', 64);
        await CreateSourceAsync(data.Source, seed: sqlite => ExecuteAsync(sqlite, $"""
            INSERT INTO vocabulary (id, word, created_at, updated_at)
                VALUES ('word', 'apple', '{Created}', '{Created}');
            INSERT INTO audio_asset (id, bucket, object_key, sha256, byte_length, content_type, created_at_unix_ms)
                VALUES ('published', 'audio', 'gb.mp3', '{hash}', 100, 'audio/mpeg', 1),
                       ('discarded', 'audio', 'us.mp3', '{hash}', 100, 'audio/mpeg', 1);
            INSERT INTO vocabulary_audio_binding (id, word_id, locale, asset_id, source_revision)
                VALUES ('binding', 'word', 'en-GB', 'published', '{hash}');
            INSERT INTO audio_upload_operation
                (idempotency_key, request_fingerprint, word_id, locale, source_revision, asset_id, state,
                 created_at_unix_ms, updated_at_unix_ms, upload_lease_fence, cleanup_lease_fence, cleanup_attempts,
                 receipt_status_code, receipt_asset_id, receipt_source_revision, receipt_error_code, completed_at_unix_ms,
                 cleanup_lease_owner, cleanup_lease_expires_at_unix_ms, cleanup_return_state)
                VALUES ('00000000-0000-0000-0000-000000000001', '{hash}', 'word', 'en-GB', '{hash}', 'published', 'Succeeded',
                        1, 2, 1, 0, 0, 200, 'published', '{hash}', NULL, 2, NULL, NULL, NULL),
                       ('00000000-0000-0000-0000-000000000002', '{hash}', 'word', 'en-US', '{hash}', 'discarded', 'CleanupClaimed',
                        1, 3, 1, 1, 1, 409, NULL, NULL, 'stale', 2, 'worker', 4, 'Abandoned');
            """));

        await using var host = CreateHost(database.ConnectionString, data.Root);
        using var client = host.CreateClient();
        using var ready = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        await using var context = database.Open();
        var binding = await context.VocabularyAudioBindings.SingleAsync(Ct);
        Assert.Equal(AudioLocale.EnGb, binding.Locale);
        var published = await context.AudioUploadOperations.SingleAsync(row => row.AssetId == binding.AssetId, Ct);
        Assert.Equal(AudioLocale.EnGb, published.Locale);
        Assert.Equal(AudioUploadState.Succeeded, published.State);
        Assert.Null(published.CleanupReturnState);
        var discarded = await context.AudioUploadOperations.SingleAsync(row => row.AssetId == "discarded", Ct);
        Assert.Equal(AudioLocale.EnUs, discarded.Locale);
        Assert.Equal(AudioUploadState.CleanupClaimed, discarded.State);
        Assert.Equal(AudioUploadState.Abandoned, discarded.CleanupReturnState);
    }

    [Fact]
    public async Task OlderSqliteSchema_IsUpgradedOnAWorkingCopy_NotInPlace()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        using var data = new DataDirectory();
        await CreateSourceAsync(data.Source, MeaningSubmissionsMigration, async sqlite =>
        {
            await ExecuteAsync(sqlite, """
                INSERT INTO vocabulary_book (id, book_name, display_order, status, created_at, updated_at)
                    VALUES ('b-old', 'Old book', 1, 1, '2026-01-01 00:00:00+00:00', '2026-01-01 00:00:00+00:00');
                INSERT INTO vocabulary (id, word, created_at, updated_at)
                    VALUES ('w-old', 'Old', '2026-01-01 00:00:00+00:00', '2026-01-01 00:00:00+00:00');
                INSERT INTO vocabulary_meaning (id, vocabulary_id, book_id, part_of_speech, meaning, created_at, updated_at)
                    VALUES ('m-old', 'w-old', 'b-old', 'ADJ', 'not new', '2026-01-01 00:00:00+00:00', '2026-01-01 00:00:00+00:00');
                """);
        });
        var before = Hash(data.Source);

        await using (var factory = CreateHost(database.ConnectionString, data.Root))
        {
            using var client = factory.CreateClient();
            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        Assert.Equal(before, Hash(data.Source));
        Assert.Equal(MeaningSubmissionsMigration, await LastSourceMigrationAsync(data.Source));
        await using var context = database.Open();
        Assert.Equal(LegacySqliteMigrationContext.LastMigration, (await context.LegacySqliteImports.SingleAsync(Ct)).SourceSchemaVersion);
        var meaning = await context.VocabularyMeanings.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("adj", meaning.NormalizedPartOfSpeech);
        Assert.Null(meaning.PhoneticUk);
    }

    [Fact]
    public async Task WithoutASource_RecordsNone_AndRefusesASourceThatAppearsLater()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        using var data = new DataDirectory();
        await using (var first = CreateHost(database.ConnectionString, data.Root))
        {
            using var client = first.CreateClient();
            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        await using (var context = database.Open())
        {
            Assert.Equal(LegacySqliteImportEntity.None, (await context.LegacySqliteImports.SingleAsync(Ct)).Outcome);
        }

        await CreateSourceAsync(data.Source, seed: SeedLatestAsync);
        using var logs = new RecordingLogs();
        await using var second = CreateHost(database.ConnectionString, data.Root);
        Assert.Throws<InvalidOperationException>(() => Logged(second, logs).CreateClient());
        Assert.Contains(LegacySqliteImporter.SourceAfterInitialization, logs.Entries);
        await using var verify = database.Open();
        Assert.False(await verify.Vocabularies.AnyAsync(Ct));
        Assert.Equal(LegacySqliteImportEntity.None, (await verify.LegacySqliteImports.SingleAsync(Ct)).Outcome);
    }

    [Fact]
    public async Task ExistingPostgreSqlData_WithoutAMarker_IsNeverMergedWithASource()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: true);
        await using (var context = database.Open())
        {
            context.VocabularyBooks.Add(new VocabularyBookEntity { Id = "pg-only", BookName = "PostgreSQL only", Status = true });
            await context.SaveChangesAsync(Ct);
        }

        using var data = new DataDirectory();
        await CreateSourceAsync(data.Source, seed: SeedLatestAsync);
        using var logs = new RecordingLogs();
        await using var host = CreateHost(database.ConnectionString, data.Root);

        Assert.Throws<InvalidOperationException>(() => Logged(host, logs).CreateClient());
        Assert.Contains(LegacySqliteImporter.TargetNotEmpty, logs.Entries);
        await using var verify = database.Open();
        Assert.Equal(["pg-only"], await verify.VocabularyBooks.Select(book => book.Id).ToListAsync(Ct));
        Assert.False(await verify.LegacySqliteImports.AnyAsync(Ct));
    }

    [Fact]
    public async Task AVerificationDifference_RollsBackEverything_AndTheNextStartCopiesAgain()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        using var data = new DataDirectory();
        await CreateSourceAsync(data.Source, seed: SeedLatestAsync);
        var before = Hash(data.Source);

        using var logs = new RecordingLogs();
        await using (var host = CreateHost(database.ConnectionString, data.Root))
        {
            var failing = Logged(host, logs).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddScoped(provider =>
                new LegacySqliteImporter(
                    provider.GetRequiredService<VocabularyDbContext>(),
                    provider.GetRequiredService<LegacySqliteImportOptions>(),
                    provider.GetRequiredService<ILogger<LegacySqliteImporter>>())
                {
                    // A change between copy and verification stands in for any
                    // conversion that does not round-trip.
                    BeforeVerification = async (connection, token) =>
                    {
                        await using var tamper = new NpgsqlCommand("UPDATE vocabulary SET phonetic_uk = 'x' WHERE id = 'w-apple'", connection);
                        await tamper.ExecuteNonQueryAsync(token);
                    }
                })));
            Assert.Throws<InvalidOperationException>(() => failing.CreateClient());
        }

        Assert.Contains("Legacy SQLite import failed: verification found a difference in table vocabulary. Nothing was committed.", logs.Entries);
        Assert.Equal(before, Hash(data.Source));
        AssertOnlyTheSourceRemains(data);
        await using (var context = database.Open())
        {
            Assert.False(await context.LegacySqliteImports.AnyAsync(Ct));
            Assert.False(await context.Vocabularies.AnyAsync(Ct));
            Assert.False(await context.VocabularyBooks.AnyAsync(Ct));
        }

        await using (var retry = CreateHost(database.ConnectionString, data.Root))
        {
            using var client = retry.CreateClient();
            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        await using var verify = database.Open();
        Assert.Equal(LegacySqliteImportEntity.Imported, (await verify.LegacySqliteImports.SingleAsync(Ct)).Outcome);
        Assert.Equal(4, await verify.Vocabularies.CountAsync(Ct));
    }

    [Fact]
    public async Task ASqliteTablePostgreSqlDoesNotHave_FailsTheImport()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        using var data = new DataDirectory();
        await CreateSourceAsync(data.Source, seed: sqlite => ExecuteAsync(sqlite, "CREATE TABLE stray (id INTEGER PRIMARY KEY)"));
        using var logs = new RecordingLogs();
        await using var host = CreateHost(database.ConnectionString, data.Root);

        Assert.Throws<InvalidOperationException>(() => Logged(host, logs).CreateClient());
        Assert.Contains("Legacy SQLite import failed: the SQLite database has table stray, which PostgreSQL does not have.", logs.Entries);
        await using var verify = database.Open();
        Assert.False(await verify.LegacySqliteImports.AnyAsync(Ct));
    }

    [Fact]
    public async Task AnUnreadableSource_FailsWithAFixedDiagnostic()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        using var data = new DataDirectory();
        await File.WriteAllTextAsync(data.Source, "not a database, and long enough to have a header page", Ct);
        using var logs = new RecordingLogs();
        await using var host = CreateHost(database.ConnectionString, data.Root);

        Assert.Throws<InvalidOperationException>(() => Logged(host, logs).CreateClient());
        Assert.Contains(LegacySqliteImporter.SourceUnreadable, logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(data.Root, StringComparison.Ordinal));
        await using var verify = database.Open();
        Assert.False(await verify.LegacySqliteImports.AnyAsync(Ct));
    }

    [Fact]
    public async Task TwoHostsStartingTogether_CopyOnce()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        using var data = new DataDirectory();
        await CreateSourceAsync(data.Source, seed: SeedLatestAsync);

        await using var first = CreateHost(database.ConnectionString, data.Root);
        await using var second = CreateHost(database.ConnectionString, data.Root);
        await Task.WhenAll(Task.Run(() => first.CreateClient().Dispose(), Ct), Task.Run(() => second.CreateClient().Dispose(), Ct));

        await using var verify = database.Open();
        Assert.Equal(LegacySqliteImportEntity.Imported, (await verify.LegacySqliteImports.SingleAsync(Ct)).Outcome);
        Assert.Equal(4, await verify.Vocabularies.CountAsync(Ct));
    }

    [Fact]
    public async Task ASourceInWalMode_IsReadWithoutLeavingSidecars()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        using var data = new DataDirectory();
        await CreateSourceAsync(data.Source, seed: SeedLatestAsync, wal: true);
        var before = Hash(data.Source);

        await using (var host = CreateHost(database.ConnectionString, data.Root))
        {
            using var client = host.CreateClient();
            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        Assert.Equal(before, Hash(data.Source));
        AssertOnlyTheSourceRemains(data);
        await using var verify = database.Open();
        Assert.Equal(4, await verify.Vocabularies.CountAsync(Ct));
    }

    [Fact]
    public void ThePlan_CoversEveryTable_PrincipalsFirst()
    {
        using var context = new VocabularyDbContext(PostgreSqlTestServer.Options("Host=localhost;Database=model").Options);
        var plan = LegacySqliteImporter.PlanTables(context.Model).Select(table => table.Name).ToList();

        Assert.Equal(34, plan.Count);
        Assert.DoesNotContain(LegacySqliteImporter.MarkerTable, plan);
        foreach (var entityType in context.Model.GetEntityTypes().Where(type => type.GetTableName() != LegacySqliteImporter.MarkerTable))
        {
            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                Assert.True(
                    plan.IndexOf(foreignKey.PrincipalEntityType.GetTableName()!) < plan.IndexOf(entityType.GetTableName()!),
                    $"{foreignKey.PrincipalEntityType.GetTableName()} must be copied before {entityType.GetTableName()}.");
            }
        }
    }

    private static VocabularyWebApplicationFactory CreateHost(string connectionString, string dataRoot) =>
        new("Testing",
            includeAppCredentials: true,
            extraConfiguration: new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString },
            keyContentRoot: dataRoot);

    /// <summary>A derived host recording log lines; the base factory disposes it.</summary>
    private static WebApplicationFactory<Program> Logged(VocabularyWebApplicationFactory host, RecordingLogs logs) =>
        host.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));

    /// <summary>Nine rows across six tables, in the shape the last SQLite release wrote them.</summary>
    private static Task SeedLatestAsync(SqliteConnection sqlite) => ExecuteAsync(sqlite, $"""
        INSERT INTO vocabulary_book (id, book_name, display_order, status, created_at, updated_at)
            VALUES ('b-1', 'Book', 1, 1, '{Created}', '{Created}');
        INSERT INTO vocabulary (id, word, created_at, updated_at) VALUES ('w-zebra', 'zebra', '{Created}', '{Created}');
        INSERT INTO vocabulary (id, word, created_at, updated_at) VALUES ('w-cafe-upper', 'CAFÉ', '{Created}', '{Created}');
        INSERT INTO vocabulary (id, word, created_at, updated_at) VALUES ('w-cafe', 'Café', '{Created}', '{Created}');
        INSERT INTO vocabulary (id, word, phonetic_uk, created_at, updated_at) VALUES ('w-apple', 'apple', '/ˈæp.əl/', '{Created}', '{Created}');
        INSERT INTO vocabulary_meaning (id, vocabulary_id, book_id, part_of_speech, meaning, created_at, updated_at)
            VALUES ('m-cafe-upper', 'w-cafe-upper', 'b-1', ' N. ', ' coffee house ', '{Created}', '{Created}');
        INSERT INTO vocabulary_meaning (id, vocabulary_id, book_id, part_of_speech, meaning, created_at, updated_at)
            VALUES ('m-cafe', 'w-cafe', 'b-1', 'n.', 'coffee house', '{Created}', '{Created}');
        INSERT INTO business_api_key (id, secret_hash, caller_id, catalog, meaning_submissions, created_at_unix_ms)
            VALUES ('key-1', '{new string('a', 64)}', 'caller', 1, 0, 1700000000000);
        INSERT INTO service_audit_logs (id, operator_source, action, target_type, target_id, outcome, occurred_at_utc)
            VALUES ('6f1c2f63-6a8e-4bb3-9a39-1f6c9a0b7d11', 'test', 'admin.sign_in', 'session', 's-1', 1, '2026-05-06 07:08:09.1234567');
        """);

    /// <summary>
    /// No working copy and no SQLite sidecar is left next to the source; the
    /// only other files are the host's own root key, when startup got that far.
    /// </summary>
    private static void AssertOnlyTheSourceRemains(DataDirectory data) =>
        Assert.DoesNotContain(Directory.GetFiles(data.Root).Select(Path.GetFileName),
            name => name is not ("vocabulary.db" or "root-key"));

    /// <summary>
    /// A SQLite source as an earlier release left it: migrated to
    /// <paramref name="targetMigration"/> (the last one by default) and, like
    /// the last SQLite release, in the rollback journal unless <paramref name="wal"/>.
    /// </summary>
    private static async Task CreateSourceAsync(
        string path, string? targetMigration = null, Func<SqliteConnection, Task>? seed = null, bool wal = false)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        await using (var context = new LegacySqliteMigrationContext(new DbContextOptionsBuilder<LegacySqliteMigrationContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options))
        {
            await context.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(targetMigration, Ct);
        }

        await using var sqlite = new SqliteConnection(connectionString);
        await sqlite.OpenAsync(Ct);
        // EF Core creates SQLite files in WAL mode.
        await ExecuteAsync(sqlite, wal ? "PRAGMA journal_mode = WAL" : "PRAGMA journal_mode = DELETE");
        if (seed is not null)
        {
            await seed(sqlite);
        }
    }

    private static async Task<string> LastSourceMigrationAsync(string path)
    {
        await using var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await sqlite.OpenAsync(Ct);
        await using var command = sqlite.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task ExecuteAsync(SqliteConnection sqlite, string sql)
    {
        await using var command = sqlite.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>An owner-only data directory, as a deployment's data/ is, holding the SQLite source.</summary>
    private sealed class DataDirectory : IDisposable
    {
        public DataDirectory()
        {
            Root = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"lexarbor-legacy-{Guid.NewGuid():N}");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string Root { get; }
        public string Source => Path.Combine(Root, "vocabulary.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();
        public IReadOnlyList<string> Entries => [.. _entries];
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void Dispose() { }

        private sealed class Recorder(RecordingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner._entries.Enqueue(formatter(state, exception));
        }
    }
}
