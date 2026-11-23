using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Lexarbor.Learning.Database;

/// <summary>Each authenticated caller owns a separate PostgreSQL namespace, including every raw statistics query.</summary>
/// <param name="interceptors">Applied to every learner context, such as the slow command log.</param>
public sealed class LearningStorage(string catalogConnection, params IInterceptor[] interceptors)
{
    // A completed upgrade stays cached; a failed one is removed so the next request retries it.
    private readonly ConcurrentDictionary<string, Lazy<Task>> upgrades = new(StringComparer.Ordinal);

    public static string Schema(string callerId) =>
        "learn_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(callerId)))[..48];

    public DbContextOptions<LearningDbContext> Options(string callerId)
    {
        var connection = new NpgsqlConnectionStringBuilder(catalogConnection) { SearchPath = Schema(callerId) };
        return new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(connection.ConnectionString)
            .AddInterceptors(interceptors).Options;
    }

    public NpgsqlConnection OpenGate(string callerId) => new(new NpgsqlConnectionStringBuilder(catalogConnection)
    {
        SearchPath = Schema(callerId),
        ApplicationName = "Lexarbor learning request gate"
    }.ConnectionString);

    /// <summary>
    /// Creates or upgrades the caller's schema once per process. The upgrade does not follow the
    /// request's cancellation or command timeout: a large schema can take longer than one request,
    /// and an abandoned request would otherwise roll it back and restart it on the next one.
    /// Concurrent requests wait for the same upgrade.
    /// </summary>
    public async Task EnsureAsync(string callerId, CancellationToken token)
    {
        var schema = Schema(callerId);
        var upgrade = upgrades.GetOrAdd(schema, _ => new Lazy<Task>(() => Task.Run(() => UpgradeAsync(callerId))));
        try
        {
            await upgrade.Value.WaitAsync(token);
        }
        catch when (upgrade.Value.IsFaulted || upgrade.Value.IsCanceled)
        {
            upgrades.TryRemove(new KeyValuePair<string, Lazy<Task>>(schema, upgrade));
            throw;
        }
    }

    private async Task UpgradeAsync(string callerId)
    {
        var schema = Schema(callerId);
        var token = CancellationToken.None;
        await using var database = new LearningDbContext(Options(callerId));
        database.Database.SetCommandTimeout(0);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({schema}, 0))", token);
        var version = await database.Database.SqlQuery<int>(
            $"SELECT CASE WHEN to_regclass({schema + ".learning_schema_version"}) IS NULL THEN 0 ELSE 1 END AS \"Value\"")
            .SingleAsync(token);
        if (version == 0)
        {
            // Only a fixed prefix and lowercase hex from SHA-256 enter the identifier.
            var createSchema = "CREATE SCHEMA " + schema;
            await database.Database.ExecuteSqlRawAsync(createSchema, token);
            await ApplyAsync(database, "SchemaV1.sql", token);
            await database.Database.ExecuteSqlRawAsync("CREATE TABLE learning_schema_version (version integer PRIMARY KEY, store_id uuid NOT NULL DEFAULT gen_random_uuid(), generation uuid); INSERT INTO learning_schema_version (version) VALUES (1)", token);
        }
        var current = await database.Database.SqlQueryRaw<int>("SELECT version AS \"Value\" FROM learning_schema_version").SingleAsync(token);
        if (current == 1)
        {
            await ApplyAsync(database, "ArchiveV2.sql", token);
            current = 2;
        }
        if (current == 2)
        {
            await ApplyAsync(database, "UnitPracticeV3.sql", token);
            current = 3;
        }
        if (current == 3)
        {
            await ApplyAsync(database, "UnitPracticeModesV4.sql", token);
            current = 4;
        }
        if (current == 4)
        {
            await ApplyAsync(database, "StorageCleanupV5.sql", token);
            current = 5;
        }
        if (current == 5) await ApplyAsync(database, "CardLocationV6.sql", token);
        else if (current != 6) throw new InvalidOperationException("Unsupported learner storage version.");
        await transaction.CommitAsync(token);
    }

    private static async Task ApplyAsync(LearningDbContext database, string file, CancellationToken token)
    {
        using var input = typeof(LearningStorage).Assembly.GetManifestResourceStream("Lexarbor.Database.Learning." + file)
            ?? throw new InvalidOperationException("Missing learner storage schema.");
        using var reader = new StreamReader(input);
        await database.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(token), token);
    }
}
