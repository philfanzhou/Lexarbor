using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lexarbor.Learning.Database;

/// <summary>Each authenticated caller owns a separate PostgreSQL namespace, including every raw statistics query.</summary>
public sealed class LearningStorage(string catalogConnection)
{
    private readonly ConcurrentDictionary<string, bool> initialized = new(StringComparer.Ordinal);

    public static string Schema(string callerId) =>
        "learn_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(callerId)))[..48];

    public DbContextOptions<LearningDbContext> Options(string callerId)
    {
        var connection = new NpgsqlConnectionStringBuilder(catalogConnection) { SearchPath = Schema(callerId) };
        return new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(connection.ConnectionString).Options;
    }

    public NpgsqlConnection OpenGate(string callerId) => new(new NpgsqlConnectionStringBuilder(catalogConnection)
    {
        SearchPath = Schema(callerId),
        ApplicationName = "Lexarbor learning request gate"
    }.ConnectionString);

    public async Task EnsureAsync(string callerId, CancellationToken token)
    {
        var schema = Schema(callerId);
        if (initialized.ContainsKey(schema)) return;
        await using var database = new LearningDbContext(Options(callerId));
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
        if (current == 1) await ApplyAsync(database, "ArchiveV2.sql", token);
        else if (current != 2) throw new InvalidOperationException("Unsupported learner storage version.");
        await transaction.CommitAsync(token);
        initialized.TryAdd(schema, true);
    }

    private static async Task ApplyAsync(LearningDbContext database, string file, CancellationToken token)
    {
        using var input = typeof(LearningStorage).Assembly.GetManifestResourceStream("Lexarbor.Database.Learning." + file)
            ?? throw new InvalidOperationException("Missing learner storage schema.");
        using var reader = new StreamReader(input);
        await database.Database.ExecuteSqlRawAsync(await reader.ReadToEndAsync(token), token);
    }
}
