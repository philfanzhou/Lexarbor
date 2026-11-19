using Lexarbor.Tests.Shared;
using Npgsql;

/// <summary>
/// The probe's databases: disposable PostgreSQL databases on the test server
/// the probe starts in a container. A probe directory records its database's
/// connection string in a file, so a crash process started on that directory
/// reaches the same database without starting a server of its own.
/// </summary>
static class ProbeDatabase
{
    private const string FileName = "database.connection";

    public static string For(string root)
    {
        var file = Path.Combine(root, FileName);
        if (File.Exists(file)) return File.ReadAllText(file);
        var connectionString = PostgreSqlTestServer.CreateDatabaseAsync().GetAwaiter().GetResult();
        File.WriteAllText(file, connectionString);
        return connectionString;
    }

    /// <summary>
    /// Copies a database as it is now, the role SQLite's online backup played:
    /// <c>CREATE DATABASE ... TEMPLATE</c> needs the source to have no other
    /// session, so the probe's own pooled connections are closed first.
    /// </summary>
    public static async Task<string> CopyAsync(string source)
    {
        var name = new NpgsqlConnectionStringBuilder(source).Database!;
        var copy = PostgreSqlTestServer.NewDatabaseName();
        NpgsqlConnection.ClearAllPools();
        await PostgreSqlTestServer.ExecuteAdministrativeAsync(
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{name}' AND pid <> pg_backend_pid()");
        await PostgreSqlTestServer.ExecuteAdministrativeAsync($"CREATE DATABASE \"{copy}\" TEMPLATE \"{name}\"");
        return await PostgreSqlTestServer.ConnectionStringAsync(copy);
    }
}
