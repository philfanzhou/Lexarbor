using System;
using System.Linq;
using System.Threading.Tasks;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// Covers the upgrade path for <c>AddVocabularyBookUnits</c>: a database at the
/// previous schema keeps every existing row untouched, keeps answering the
/// existing queries, and can store units and assignments afterwards. A database
/// created from scratch by the full migration chain builds the same structure.
/// </summary>
public sealed class VocabularyBookUnitMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260821023350_AddNormalizedWordColumn";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"lexarbor-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Upgrade_KeepsExistingRowsAndQueries_AndStoresUnitsAfterwards()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "upgraded.db");

        await using (var context = CreateContext(databasePath))
        {
            context.GetService<IMigrator>().Migrate(PreviousMigration);

            // Stands in for a database an earlier release filled in: two books,
            // two words, three meanings.
            var now = DateTimeOffset.UtcNow;
            context.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "book-a", BookName = "Book A", Status = true, CreatedAt = now, UpdatedAt = now
            });
            context.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "book-b", BookName = "Book B", Status = true, CreatedAt = now, UpdatedAt = now
            });
            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = "word-apple", Word = "apple", CreatedAt = now, UpdatedAt = now
            });
            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = "word-come-true", Word = "come true", CreatedAt = now, UpdatedAt = now
            });
            context.VocabularyMeanings.Add(new VocabularyMeaningEntity
            {
                Id = "meaning-1", VocabularyId = "word-apple", BookId = "book-a",
                PartOfSpeech = "n.", Meaning = "苹果", CreatedAt = now, UpdatedAt = now
            });
            context.VocabularyMeanings.Add(new VocabularyMeaningEntity
            {
                Id = "meaning-2", VocabularyId = "word-come-true", BookId = "book-a",
                PartOfSpeech = "phrase", Meaning = "（梦想等）实现", CreatedAt = now, UpdatedAt = now
            });
            context.VocabularyMeanings.Add(new VocabularyMeaningEntity
            {
                Id = "meaning-3", VocabularyId = "word-apple", BookId = "book-b",
                PartOfSpeech = "n.", Meaning = "苹果", CreatedAt = now, UpdatedAt = now
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var before = await ReadSnapshotAsync(databasePath);
        (int bookAWordCount, string resolvedWord) = await ReadQueryResultsAsync(databasePath);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var after = await ReadSnapshotAsync(databasePath);
        Assert.Equal(before.Books, after.Books);
        Assert.Equal(before.Words, after.Words);
        Assert.Equal(before.Meanings, after.Meanings);
        Assert.Equal(2, after.Books.Count);
        Assert.Equal(2, after.Words.Count);
        Assert.Equal(3, after.Meanings.Count);
        (int bookAWordCountAfter, string resolvedWordAfter) = await ReadQueryResultsAsync(databasePath);
        Assert.Equal(bookAWordCount, bookAWordCountAfter);
        Assert.Equal(resolvedWord, resolvedWordAfter);

        // The upgraded database now stores units and assignments: eight units
        // across two books, one meaning assigned to two units of its book.
        await using (var context = CreateContext(databasePath))
        {
            var unitOfWork = new UnitOfWork(context);
            var unitService = new VocabularyBookUnitDomainService(
                new VocabularyBookRepository(context),
                new VocabularyMeaningRepository(context),
                new VocabularyBookUnitRepository(context),
                new VocabularyMeaningUnitRepository(context),
                unitOfWork);

            for (var number = 1; number <= 6; number++)
            {
                await unitService.CreateAsync("book-a", number, null);
            }
            var unit2InB = await unitService.CreateAsync("book-b", 2, null);
            var unit6InB = await unitService.CreateAsync("book-b", 6, null);
            Assert.Equal(6, (await unitService.GetByBookAsync("book-a")).Count);
            Assert.Equal(2, (await unitService.GetByBookAsync("book-b")).Count);

            await unitService.AssignMeaningAsync(unit2InB.Id, "meaning-3");
            await unitService.AssignMeaningAsync(unit6InB.Id, "meaning-3");
            Assert.Equal(2, await context.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task NewDatabase_BuiltByTheFullMigrationChain_SupportsUnits()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "fresh.db");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = CreateContext(databasePath))
        {
            var unitOfWork = new UnitOfWork(context);
            var bookRepository = new VocabularyBookRepository(context);
            var unitService = new VocabularyBookUnitDomainService(
                bookRepository,
                new VocabularyMeaningRepository(context),
                new VocabularyBookUnitRepository(context),
                new VocabularyMeaningUnitRepository(context),
                unitOfWork);

            var now = DateTimeOffset.UtcNow;
            await bookRepository.AddAsync(new VocabularyBookModel
            {
                Id = "book", BookName = "Book", Status = true, CreatedAt = now, UpdatedAt = now
            });
            await unitOfWork.SaveChangesAsync();

            for (var number = 1; number <= 8; number++)
            {
                await unitService.CreateAsync("book", number, null);
            }

            Assert.Equal(8, (await unitService.GetByBookAsync("book")).Count);
            Assert.Equal(
                Enumerable.Range(1, 8),
                context.VocabularyBookUnits.AsNoTracking().Select(unit => unit.Number).OrderBy(number => number));
        }
    }

    /// <summary>Reads the stored rows over a connection of its own, so the
    /// comparison is against what is in the file rather than what EF tracks.
    /// </summary>
    private static async Task<(List<string> Books, List<string> Words, List<string> Meanings)> ReadSnapshotAsync(
        string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        List<string> Read(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
            }

            return rows;
        }

        return (Read("SELECT id, book_name, status FROM vocabulary_book ORDER BY id"),
            Read("SELECT id, word, phonetic_uk, phonetic_us FROM vocabulary ORDER BY id"),
            Read("SELECT id, vocabulary_id, book_id, part_of_speech, meaning, example FROM vocabulary_meaning ORDER BY id"));
    }

    private static async Task<(int BookAWordCount, string ResolvedWord)> ReadQueryResultsAsync(
        string databasePath)
    {
        await using var context = CreateContext(databasePath);
        var resolved = await new VocabularyRepository(context).GetByNormalizedWordAsync("come true");
        var (words, totalCount) = await new VocabularyBookRepository(context).GetWordsAsync("book-a", 1, 20);
        return (totalCount, resolved!.Word);
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
        return new VocabularyDbContext(
            new DbContextOptionsBuilder<VocabularyDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options);
    }
}
