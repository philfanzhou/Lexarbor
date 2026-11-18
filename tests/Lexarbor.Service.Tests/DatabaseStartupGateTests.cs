using System.Net;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Lexarbor.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Pins the ServiceMantle PostgreSQL startup gate: a missing database is
/// created and migrated, an empty one is migrated, later starts skip the
/// executor, a newer schema and a failing migration stop startup with fixed
/// safe diagnostics that never echo the connection string, an unreachable
/// server fails closed, two hosts starting together migrate once under the
/// advisory lock, and disabled initialization keeps the host up but not ready.
/// Every case goes through the real host startup against the disposable test
/// server.
/// </summary>
public class DatabaseStartupGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VocabularyWebApplicationFactory CreateHost(
        string connectionString,
        string? keyContentRoot = null,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var configuration = new Dictionary<string, string?>(extra ?? new Dictionary<string, string?>())
        {
            ["ConnectionStrings:Default"] = connectionString
        };
        return new("Testing", includeAppCredentials: true, extraConfiguration: configuration, keyContentRoot: keyContentRoot);
    }

    private static string CreateSharedKeyRoot(string name)
    {
        var keyRoot = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"lexarbor-gate-keys-{name}-{Guid.NewGuid():N}");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return keyRoot;
    }

    private static async Task<List<string>> AppliedMigrationsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY 1", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var applied = new List<string>();
        while (await reader.ReadAsync(Ct)) applied.Add(reader.GetString(0));
        return applied;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static string[] KnownMigrations()
    {
        using var context = new VocabularyDbContext(PostgreSqlTestServer.Options("Host=localhost;Database=model").Options);
        return context.Database.GetMigrations().Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task FirstStart_WithoutTheDatabase_CreatesAndMigratesIt()
    {
        var connectionString = await PostgreSqlTestServer.ConnectionStringAsync(PostgreSqlTestServer.NewDatabaseName());
        try
        {
            await using var factory = CreateHost(connectionString);
            using var client = factory.CreateClient();

            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Equal(KnownMigrations(), await AppliedMigrationsAsync(connectionString));
        }
        finally
        {
            await PostgreSqlTestServer.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task FirstStart_OnAnEmptyDatabase_Migrates()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        await using var factory = CreateHost(database.ConnectionString);
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(KnownMigrations(), await AppliedMigrationsAsync(database.ConnectionString));
    }

    [Fact]
    public async Task SecondStart_OnMigratedDatabase_SkipsTheExecutorAndKeepsData()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var keys = CreateSharedKeyRoot("second-start");
        try
        {
            await using (var first = CreateHost(database.ConnectionString, keys))
            {
                using var client = first.CreateClient();
                await using var scope = first.Services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
                context.VocabularyBooks.Add(new VocabularyBookEntity { Id = "kept", BookName = "Kept", Status = true });
                await context.SaveChangesAsync(Ct);
            }

            await using var second = CreateHost(database.ConnectionString, keys);
            using var secondClient = second.CreateClient();
            using var ready = await secondClient.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            await using var verify = database.Open();
            Assert.True(await verify.VocabularyBooks.AnyAsync(book => book.Id == "kept", Ct));
            Assert.Equal(KnownMigrations(), await AppliedMigrationsAsync(database.ConnectionString));
        }
        finally
        {
            Directory.Delete(keys, recursive: true);
        }
    }

    [Fact]
    public async Task TwoHostsStartingTogether_MigrateOnce()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        var keys = CreateSharedKeyRoot("concurrent");
        try
        {
            await using var first = CreateHost(database.ConnectionString, keys);
            await using var second = CreateHost(database.ConnectionString, keys);
            // Factory startup is serialized for its environment passthrough,
            // so the hosts are built on separate threads and the gate's
            // advisory lock, not the factory, decides who migrates.
            var clients = await Task.WhenAll(
                Task.Run(first.CreateClient, Ct),
                Task.Run(second.CreateClient, Ct));
            foreach (var client in clients)
            {
                using var ready = await client.GetAsync("/health/ready", Ct);
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
                client.Dispose();
            }

            Assert.Equal(KnownMigrations(), await AppliedMigrationsAsync(database.ConnectionString));
        }
        finally
        {
            Directory.Delete(keys, recursive: true);
        }
    }

    [Fact]
    public async Task NewerSchemaVersion_RefusesStartupWithoutTouchingTheDatabase()
    {
        await using var database = new PostgreSqlTestDatabase();
        const string futureMigration = "99991231235959_FromTheFuture";
        await ExecuteAsync(database.ConnectionString,
            $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('{futureMigration}', '10.0.0')");

        await using var factory = CreateHost(database.ConnectionString);
        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("migration.version_too_new", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(database.Name, failure.Message, StringComparison.Ordinal);
        Assert.Contains(futureMigration, await AppliedMigrationsAsync(database.ConnectionString));
    }

    [Fact]
    public async Task FailingMigration_StopsStartupWithSafeDiagnostic()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        // A schema without history inspects as empty, so the executor runs
        // and its migration fails on the pre-existing table.
        await ExecuteAsync(database.ConnectionString, "CREATE TABLE vocabulary (id text NOT NULL)");

        await using var factory = CreateHost(database.ConnectionString);
        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("migration.execution_failed", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(database.Name, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableServer_FailsStartupWithoutEchoingTheConnectionString()
    {
        const string connectionString = "Host=127.0.0.1;Port=1;Database=lexarbor_unreachable;Username=probe;Password=synthetic-secret-marker;Timeout=2";
        await using var factory = CreateHost(connectionString);
        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.StartsWith("The database startup migration was refused: ", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret-marker", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("lexarbor_unreachable", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledInitialization_KeepsHostRunningNotReadyAndDatabaseUntouched()
    {
        await using var database = new PostgreSqlTestDatabase(migrated: false);
        await using var factory = CreateHost(database.ConnectionString,
            extra: new Dictionary<string, string?> { ["Database:InitializeOnStartup"] = "false" });
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct));
        Assert.Equal("not_ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("notStarted", body.RootElement.GetProperty("migrationStatus").GetString());

        await using var connection = await database.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('\"__EFMigrationsHistory\"') IS NULL", connection);
        Assert.Equal(true, await command.ExecuteScalarAsync(Ct));
    }
}
