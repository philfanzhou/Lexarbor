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
    public async Task InitializeAsync_MissingDatabase_CreatesEmptySchema()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "vocabulary.db");

        await using var context = CreateContext(databasePath);
        await DatabaseInitializer.InitializeAsync(
            context,
            NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(databasePath));
        Assert.Equal(0, await context.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await context.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await context.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            context.Database.GetMigrations(),
            await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitializeAsync_ExistingDatabaseWithData_LeavesDataUnchanged()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "populated.db");

        // Stands in for a database created by an earlier release, which loaded
        // the Starter English 300 book on its first start. A later start must
        // neither add to it nor remove it.
        await using (var context = CreateContext(databasePath))
        {
            await DatabaseInitializer.InitializeAsync(
                context,
                NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

            var now = DateTimeOffset.UtcNow;
            var vocabularyId = Guid.NewGuid().ToString();
            context.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "starter-english-300",
                BookName = "Starter English 300",
                Category = "Starter",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = vocabularyId,
                Word = "apple",
                PhoneticUk = "/ˈæp.əl/",
                PhoneticUs = "/ˈæp.əl/",
                CreatedAt = now,
                UpdatedAt = now
            });
            context.VocabularyMeanings.Add(new VocabularyMeaningEntity
            {
                Id = Guid.NewGuid().ToString(),
                VocabularyId = vocabularyId,
                BookId = "starter-english-300",
                PartOfSpeech = "n.",
                Meaning = "苹果",
                CreatedAt = now,
                UpdatedAt = now
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var before = await ReadSnapshotAsync(databasePath, TestContext.Current.CancellationToken);

        await using (var context = CreateContext(databasePath))
        {
            await DatabaseInitializer.InitializeAsync(
                context,
                NullLoggerFactory.Instance, TestContext.Current.CancellationToken);
        }

        var after = await ReadSnapshotAsync(databasePath, TestContext.Current.CancellationToken);
        Assert.Equal(before.Books, after.Books);
        Assert.Equal(before.Vocabularies, after.Vocabularies);
        Assert.Equal(before.Meanings, after.Meanings);
        Assert.Equal(["starter-english-300|Starter English 300"], after.Books);
        Assert.Equal(["apple|/ˈæp.əl/|/ˈæp.əl/"], after.Vocabularies);
        Assert.Equal(["starter-english-300|n.|苹果"], after.Meanings);
    }

    [Fact]
    public async Task InitializeAsync_ExistingEmptyFile_MigratesWithoutWritingData()
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
        Assert.Equal(0, await context.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DatabaseAssembly_ShipsNoVocabularyData()
    {
        Assert.DoesNotContain(
            typeof(VocabularyDbContext).Assembly.GetManifestResourceNames(),
            name => name.Contains("starter-vocabulary", StringComparison.OrdinalIgnoreCase));
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

    private static async Task<DatabaseSnapshot> ReadSnapshotAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext(databasePath);
        var books = await context.VocabularyBooks
            .OrderBy(item => item.Id)
            .Select(item => item.Id + "|" + item.BookName)
            .ToListAsync(cancellationToken);
        var vocabularies = await context.Vocabularies
            .OrderBy(item => item.Id)
            .Select(item => item.Word + "|" + item.PhoneticUk + "|" + item.PhoneticUs)
            .ToListAsync(cancellationToken);
        var meanings = await context.VocabularyMeanings
            .OrderBy(item => item.Id)
            .Select(item => item.BookId + "|" + item.PartOfSpeech + "|" + item.Meaning)
            .ToListAsync(cancellationToken);
        return new DatabaseSnapshot(books, vocabularies, meanings);
    }

    private sealed record DatabaseSnapshot(
        IReadOnlyList<string> Books,
        IReadOnlyList<string> Vocabularies,
        IReadOnlyList<string> Meanings);

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
