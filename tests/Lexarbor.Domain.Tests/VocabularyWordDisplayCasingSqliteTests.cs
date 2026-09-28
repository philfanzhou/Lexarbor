using System;
using System.Linq;
using System.Threading.Tasks;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// Covers the display-spelling rule against SQLite rows written by an earlier
/// release, which lower-cased every word on import: those values keep their
/// spelling, later equivalent imports reuse their rows without rewriting them,
/// and only newly created words carry the imported casing. Concurrent
/// equivalent imports are covered by
/// <see cref="SqliteConcurrencyTests.ConcurrentEquivalentImports_AreIdempotent"/>,
/// which imports " Apple " against "APPLE".
/// </summary>
public sealed class VocabularyWordDisplayCasingSqliteTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"lexarbor-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task LegacyLowercaseRows_KeepTheirValueWhileNewWordsKeepImportedCasing()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "legacy-casing.db");

        // Rows the previous release would have written: every word lower-cased.
        await using (var setup = CreateContext(databasePath))
        {
            await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            await setup.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO vocabulary_book (id, book_name, description, publisher, education_level, grade,
                                             category, display_order, status, icon_url, created_at, updated_at)
                VALUES ('book', 'Book', null, null, null, null, null, 0, 1, null,
                        '2026-01-01 00:00:00.0000000+00:00', '2026-01-01 00:00:00.0000000+00:00')
                """,
                TestContext.Current.CancellationToken);
            await InsertLegacyWordAsync(setup, "legacy-apple", "apple");
            await InsertLegacyWordAsync(setup, "legacy-nobel-prize", "nobel prize");
        }

        await using (var context = CreateContext(databasePath))
        {
            var service = CreateService(context);

            // An equivalent import resolves to the legacy row and leaves its
            // lower-case spelling exactly as it is; the original casing is not
            // recoverable and nothing guesses at it.
            var (reused, _) = await service.AddOrUpdateAsync(
                new VocabularyModel { Word = "APPLE" },
                new VocabularyMeaningModel { BookId = "book", PartOfSpeech = "n.", Meaning = "苹果" });
            Assert.Equal("legacy-apple", reused.Id);
            Assert.Equal(
                "apple",
                await StoredWordAsync(context, "legacy-apple"));

            // A new word keeps the imported display casing while remaining
            // resolvable through its normalized key.
            var (created, _) = await service.AddOrUpdateAsync(
                new VocabularyModel { Word = "  Come True  " },
                new VocabularyMeaningModel { BookId = "book", PartOfSpeech = "phrase", Meaning = "（梦想等）实现" });
            Assert.Equal(
                "Come True",
                await StoredWordAsync(context, created.Id));
            Assert.NotNull(await new VocabularyRepository(context).GetByNormalizedWordAsync("come true"));

            // Apple / ' apple ' / APPLE still share one row, and re-importing
            // does not create a second one.
            await service.AddOrUpdateAsync(
                new VocabularyModel { Word = " apple " },
                new VocabularyMeaningModel { BookId = "book", PartOfSpeech = "n.", Meaning = "苹果" });
            await service.AddOrUpdateAsync(
                new VocabularyModel { Word = "APPLE" },
                new VocabularyMeaningModel { BookId = "book", PartOfSpeech = "n.", Meaning = "苹果" });
            Assert.Equal(
                1,
                await context.Vocabularies.CountAsync(
                    word => word.NormalizedWord == "apple",
                    TestContext.Current.CancellationToken));
            Assert.Equal(
                "apple",
                await StoredWordAsync(context, "legacy-apple"));
        }
    }

    private static Task<string> StoredWordAsync(VocabularyDbContext context, string id)
    {
        return context.Vocabularies
            .AsNoTracking()
            .Where(word => word.Id == id)
            .Select(word => word.Word)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertLegacyWordAsync(VocabularyDbContext context, string id, string word)
    {
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO vocabulary (id, word, phonetic_uk, phonetic_us, created_at, updated_at)
            VALUES ({0}, {1}, null, null,
                    '2026-01-01 00:00:00.0000000+00:00', '2026-01-01 00:00:00.0000000+00:00')
            """,
            [id, word],
            TestContext.Current.CancellationToken);
    }

    private static VocabularyDomainService CreateService(VocabularyDbContext context)
    {
        return new VocabularyDomainService(
            new VocabularyRepository(context),
            new VocabularyBookRepository(context),
            new VocabularyMeaningRepository(context),
            new UnitOfWork(context));
    }

    private static VocabularyDbContext CreateContext(string databasePath)
    {
        return new VocabularyDbContext(
            new DbContextOptionsBuilder<VocabularyDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options);
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
}
