using Npgsql;

namespace Lexarbor.Host;

internal static class TransitionUpgradeGuard
{
    internal const string UpgradeRequired =
        "A legacy database requires migration. Upgrade through v0.2.0-rc.2 before starting this release.";

    internal static async Task CheckAsync(string? configuredPath, string contentRoot, string connectionString)
    {
        try
        {
            var path = Path.GetFullPath(string.IsNullOrEmpty(configuredPath)
                ? Path.Combine("data", "vocabulary.db") : configuredPath, contentRoot);
            // Unlike File.Exists, an inaccessible directory must not look like a fresh installation.
            File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("The legacy database location could not be checked.");
        }

        // Check before database creation and even when startup migrations are disabled.
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM legacy_sqlite_import WHERE id = 1 AND outcome = 'imported')", connection);
            if (await command.ExecuteScalarAsync() is true)
            {
                return;
            }
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.InvalidCatalogName or PostgresErrorCodes.UndefinedTable)
        {
            throw new InvalidOperationException(UpgradeRequired);
        }
        catch (NpgsqlException)
        {
            throw new InvalidOperationException("The PostgreSQL migration marker could not be checked.");
        }

        throw new InvalidOperationException(UpgradeRequired);
    }
}
