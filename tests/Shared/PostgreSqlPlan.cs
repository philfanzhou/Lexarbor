using System.Data.Common;
using Npgsql;

namespace Lexarbor.Tests.Shared;

/// <summary>
/// Reads PostgreSQL's plan for a statement the application actually sent.
/// </summary>
/// <remarks>
/// Test tables hold a handful of rows, where a sequential scan is always the
/// cheapest plan, so the planner is told to avoid one whenever it can: a plan
/// that still scans the table has no index able to answer the predicate, which
/// is the property these tests guard.
/// </remarks>
public static class PostgreSqlPlan
{
    public static async Task<string> ExplainAsync(string connectionString, string sql, IEnumerable<DbParameter> parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var disable = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await disable.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand("EXPLAIN " + sql, connection);
        foreach (var parameter in parameters)
            command.Parameters.Add(((NpgsqlParameter)parameter).Clone());
        await using var reader = await command.ExecuteReaderAsync();
        var plan = new List<string>();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(0));
        return string.Join(Environment.NewLine, plan);
    }
}
