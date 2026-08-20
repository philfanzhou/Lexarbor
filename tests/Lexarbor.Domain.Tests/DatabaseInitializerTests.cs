using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"lexarbor-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InitializeAsync_MissingDatabase_CreatesSchemaAndStarterBook()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "vocabulary.db");

        await using (var context = CreateContext(databasePath))
        {
            await DatabaseInitializer.InitializeAsync(
                context,
                NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(databasePath));
            Assert.Equal(1, await context.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(300, await context.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(300, await context.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
            Assert.All(
                await context.Vocabularies.ToListAsync(TestContext.Current.CancellationToken),
                item =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.PhoneticUk));
                    Assert.False(string.IsNullOrWhiteSpace(item.PhoneticUs));
                });
        }

        await using (var context = CreateContext(databasePath))
        {
            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = Guid.NewGuid().ToString(),
                Word = "persisted",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await DatabaseInitializer.InitializeAsync(
                context,
                NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

            Assert.Equal(301, await context.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await context.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task InitializeAsync_ExistingEmptyFile_MigratesWithoutLoadingSeed()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "existing.db");
        await File.WriteAllBytesAsync(databasePath, [], TestContext.Current.CancellationToken);

        await using var context = CreateContext(databasePath);
        await DatabaseInitializer.InitializeAsync(
            context,
            NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(0, await context.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await context.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializeAsync_EnablesWriteAheadLogging()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "journal.db");

        await using (var context = CreateContext(databasePath))
        {
            await DatabaseInitializer.InitializeAsync(
                context,
                NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
        }

        // Read on a connection that did nothing to set it, so this proves the
        // mode was persisted into the database header rather than applied to the
        // one connection that ran the PRAGMA. Under the default rollback journal
        // this reads "delete", and every anonymous read contended with every
        // administrative write for the same file lock.
        await using (var context = CreateContext(databasePath))
        {
            Assert.Equal(
                "wal",
                await ReadJournalModeAsync(context, TestContext.Current.CancellationToken));
        }
    }

    private static async Task<string?> ReadJournalModeAsync(
        VocabularyDbContext context,
        CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            return (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    public void Dispose()
    {
        var resolvedTemporaryDirectory = Path.GetFullPath(_temporaryDirectory);
        var resolvedSystemTemporaryDirectory = Path.GetFullPath(Path.GetTempPath());
        if (resolvedTemporaryDirectory.StartsWith(
                resolvedSystemTemporaryDirectory,
                StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(resolvedTemporaryDirectory))
        {
            Directory.Delete(resolvedTemporaryDirectory, recursive: true);
        }
    }

    private static VocabularyDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        return new VocabularyDbContext(options);
    }
}
