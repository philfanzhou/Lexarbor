using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(
        VocabularyDbContext context,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        var logger = loggerFactory.CreateLogger(nameof(DatabaseInitializer));
        var databasePath = GetDatabasePath(context);
        var isNewDatabase = databasePath != null && !File.Exists(databasePath);

        if (databasePath != null)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        await context.Database.MigrateAsync(cancellationToken);

        if (databasePath != null)
        {
            var journalMode = await EnableWriteAheadLoggingAsync(context, cancellationToken);
            logger.LogInformation("SQLite journal mode is {JournalMode}", journalMode);
        }

        if (isNewDatabase)
        {
            await VocabularySeedData.ApplyAsync(context, cancellationToken);
            logger.LogInformation(
                "Created SQLite database and loaded the bundled starter vocabulary at {DatabasePath}",
                databasePath);
        }
        else
        {
            logger.LogInformation(
                "Applied SQLite migrations without reloading starter vocabulary");
        }
    }

    /// <summary>
    /// Switches the database to write-ahead logging, under which a reader no
    /// longer blocks a writer. The anonymous detail and question endpoints read
    /// on every request, so under the default rollback journal they contended
    /// with every administrative write for the same file lock.
    /// </summary>
    /// <remarks>
    /// The setting lives in the database header rather than the connection, so
    /// this is a no-op after the first start. It is run on every start anyway so
    /// that a database restored from a backup taken elsewhere is switched over
    /// too. An in-memory database has no journal to switch and is skipped by the
    /// caller. WAL needs shared memory beside the database file, which rules out
    /// some network filesystems; the deployment guide says so.
    /// </remarks>
    private static async Task<string?> EnableWriteAheadLoggingAsync(
        VocabularyDbContext context,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = context.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            // PRAGMA cannot be composed into a query, so it goes through the
            // raw command rather than through SqlQuery.
            command.CommandText = "PRAGMA journal_mode=WAL;";
            return (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static string? GetDatabasePath(VocabularyDbContext context)
    {
        var connectionString = context.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "The SQLite connection string is not configured.");
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new InvalidOperationException(
                "The SQLite data source is not configured.");
        }

        return string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetFullPath(builder.DataSource);
    }
}
