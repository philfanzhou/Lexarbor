using Lexarbor.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Lexarbor.Tests.Shared;

/// <summary>
/// One disposable PostgreSQL server per test run, started on first use in a
/// container, and one fresh database per test copied from a migrated
/// template. Tests run against the same server, provider and migrations as
/// production; there is no in-memory or file-database substitute.
/// </summary>
/// <remarks>
/// The server keeps its default (linguistic) database collation on purpose:
/// Lexarbor's ordering and normalization must hold because every text column
/// is declared <c>COLLATE "C"</c>, not because the database happens to have
/// been created with it. Durability settings are relaxed because the data
/// directory is thrown away with the container. Each per-test database is a
/// template copy of roughly 8 MB and is dropped when its test ends.
/// </remarks>
public static class PostgreSqlTestServer
{
    /// <summary>The oldest supported server; override with LEXARBOR_TEST_POSTGRES_IMAGE.</summary>
    public const string DefaultImage = "postgres:16-alpine";

    private const string TemplateDatabase = "lexarbor_template";
    private static readonly SemaphoreSlim CreateLock = new(1, 1);
    private static readonly Lazy<Task<PostgreSqlContainer>> Server = new(StartAsync);
    private static string? _templateMigrated;

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        var image = Environment.GetEnvironmentVariable("LEXARBOR_TEST_POSTGRES_IMAGE");
        var container = new PostgreSqlBuilder(string.IsNullOrWhiteSpace(image) ? DefaultImage : image)
            .WithDatabase("postgres")
            .WithUsername("lexarbor")
            .WithPassword("lexarbor-test")
            .WithCommand("-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off",
                "-c", "max_connections=1000")
            .Build();
        await container.StartAsync();
        return container;
    }

    /// <summary>A connection string for <paramref name="database"/> on the shared server.</summary>
    public static async Task<string> ConnectionStringAsync(string database)
    {
        var server = await Server.Value;
        // Hundreds of short-lived databases each get a pool of their own. Small
        // pools that close idle connections quickly keep the server's (and a
        // VM-based Docker's port forwarder's) connection count bounded.
        return new NpgsqlConnectionStringBuilder(server.GetConnectionString())
        {
            Database = database,
            MaxPoolSize = 10,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1
        }.ConnectionString;
    }

    /// <summary>The administrative connection string (the <c>postgres</c> database).</summary>
    public static Task<string> AdministrativeConnectionStringAsync() => ConnectionStringAsync("postgres");

    /// <summary>A database name no test has used.</summary>
    public static string NewDatabaseName() => $"lexarbor_test_{Guid.NewGuid():N}";

    /// <summary>
    /// Creates a database for one test. A migrated database is a copy of the
    /// template, which the first caller migrates once; an empty one has no
    /// schema at all.
    /// </summary>
    public static async Task<string> CreateDatabaseAsync(bool migrated = true)
    {
        var name = NewDatabaseName();
        await CreateLock.WaitAsync();
        try
        {
            if (migrated)
            {
                await EnsureTemplateAsync();
            }

            await ExecuteAdministrativeAsync(migrated
                ? $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\""
                : $"CREATE DATABASE \"{name}\"");
        }
        finally
        {
            CreateLock.Release();
        }

        return await ConnectionStringAsync(name);
    }

    /// <summary>Drops a test database, closing any connection still open on it.</summary>
    public static async Task DropDatabaseAsync(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
        await ExecuteAdministrativeAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
    }

    /// <summary>Options for a context on <paramref name="connectionString"/>.</summary>
    public static DbContextOptionsBuilder<VocabularyDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<VocabularyDbContext>().UseNpgsql(connectionString);

    /// <summary>
    /// A trigger that refuses every row with SQLSTATE 23000, the shape a
    /// database rule's refusal takes; a test injects one to fail a write at a
    /// chosen table. The name is a test constant, never input.
    /// </summary>
    public static string RejectingTriggerSql(string name, string timing, string table) => $"""
        CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'synthetic' USING ERRCODE = '23000'; END $$;
        CREATE TRIGGER {name} {timing} ON {table} FOR EACH ROW EXECUTE FUNCTION {name}();
        """;

    public static async Task ExecuteAdministrativeAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(await AdministrativeConnectionStringAsync());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsureTemplateAsync()
    {
        if (_templateMigrated is not null)
        {
            return;
        }

        await ExecuteAdministrativeAsync($"DROP DATABASE IF EXISTS \"{TemplateDatabase}\" WITH (FORCE)");
        await ExecuteAdministrativeAsync($"CREATE DATABASE \"{TemplateDatabase}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(await ConnectionStringAsync(TemplateDatabase))
        {
            Pooling = false
        }.ConnectionString;
        await using (var context = new VocabularyDbContext(Options(connectionString).Options))
        {
            await context.Database.MigrateAsync();
        }

        _templateMigrated = connectionString;
    }
}

/// <summary>
/// A database owned by one test: created on construction (synchronously, as
/// xUnit constructors are), dropped on disposal.
/// </summary>
public sealed class PostgreSqlTestDatabase : IDisposable, IAsyncDisposable
{
    public PostgreSqlTestDatabase(bool migrated = true)
    {
        ConnectionString = PostgreSqlTestServer.CreateDatabaseAsync(migrated).GetAwaiter().GetResult();
    }

    public string ConnectionString { get; }

    public string Name => new NpgsqlConnectionStringBuilder(ConnectionString).Database!;

    public VocabularyDbContext Open(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(PostgreSqlTestServer.Options(ConnectionString).AddInterceptors(interceptors).Options);

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public void Dispose() => PostgreSqlTestServer.DropDatabaseAsync(ConnectionString).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync() => await PostgreSqlTestServer.DropDatabaseAsync(ConnectionString);
}

/// <summary>
/// Counts the rows written to a test database, the role SQLite's
/// <c>total_changes()</c> played: a row-level trigger on every application
/// table adds one per inserted, updated or deleted row.
/// </summary>
public static class PostgreSqlChangeCounter
{
    public static async Task<long> ReadAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var install = new NpgsqlCommand("""
            DO $$
            DECLARE t record;
            BEGIN
                IF to_regclass('lexarbor_test_changes') IS NULL THEN
                    CREATE TABLE lexarbor_test_changes (id int PRIMARY KEY, total bigint NOT NULL);
                    INSERT INTO lexarbor_test_changes VALUES (1, 0);
                    CREATE FUNCTION lexarbor_test_count_change() RETURNS trigger LANGUAGE plpgsql AS $f$
                    BEGIN UPDATE lexarbor_test_changes SET total = total + 1 WHERE id = 1; RETURN NULL; END $f$;
                    FOR t IN SELECT tablename FROM pg_tables
                             WHERE schemaname = 'public' AND tablename NOT IN ('lexarbor_test_changes', '__EFMigrationsHistory') LOOP
                        EXECUTE format('CREATE TRIGGER lexarbor_test_count_change AFTER INSERT OR UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION lexarbor_test_count_change()', t.tablename);
                    END LOOP;
                END IF;
            END $$;
            """, connection))
            await install.ExecuteNonQueryAsync();
        await using var read = new NpgsqlCommand("SELECT total FROM lexarbor_test_changes WHERE id = 1", connection);
        return (long)(await read.ExecuteScalarAsync())!;
    }
}

/// <summary>
/// Databases shared by several hosts of one test under a name of the test's
/// choosing — a restart, a second host on the same data — the way two hosts
/// used to share one SQLite file path. Each name maps to one migrated
/// database for the rest of the run; the container is discarded afterwards.
/// </summary>
public static class SharedTestDatabases
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<string>> Databases = new(StringComparer.Ordinal);

    /// <summary>The same database with a bounded lock wait, as the host's own configuration adds.</summary>
    public static string WithLockTimeout(string connectionString, string timeout) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Options = $"-c lock_timeout={timeout}" }.ConnectionString;

    public static string ConnectionString(string name) =>
        Databases.GetOrAdd(name, _ => new Lazy<string>(() =>
            PostgreSqlTestServer.CreateDatabaseAsync().GetAwaiter().GetResult())).Value;
}

/// <summary>
/// Storage failures a test injects where the provider would raise them: a
/// lock wait past its timeout (busy) and an unclassified server error.
/// </summary>
public static class SyntheticStorageErrors
{
    /// <summary>
    /// What another writer holding the catalog looks like: inside an open
    /// transaction, every catalog table is locked against writes while plain
    /// reads continue. (SQLite's equivalent was a held BEGIN IMMEDIATE.)
    /// </summary>
    public const string CatalogWriterLockSql =
        "LOCK TABLE vocabulary_book, vocabulary, vocabulary_meaning, vocabulary_book_unit, vocabulary_meaning_unit, vocabulary_relation IN EXCLUSIVE MODE";

    public static PostgresException Busy(string message) =>
        new(message, "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable);

    public static PostgresException Failure(string message) =>
        new(message, "ERROR", "ERROR", PostgresErrorCodes.InternalError);
}
