using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.Vocabulary.Database;

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
