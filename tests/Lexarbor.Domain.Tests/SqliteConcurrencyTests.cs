using Microsoft.EntityFrameworkCore;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class SqliteConcurrencyTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"lexarbor-concurrency-{Guid.NewGuid():N}");

    [Fact]
    public async Task ConcurrentEquivalentImports_AreIdempotent()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "concurrency.db");
        await using (var setupContext = CreateContext(databasePath))
        {
            await setupContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            setupContext.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "concurrency-book",
                BookName = "Concurrency Book",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var firstContext = CreateContext(databasePath);
        await using var secondContext = CreateContext(databasePath);
        var firstService = CreateService(firstContext);
        var secondService = CreateService(secondContext);

        var results = await Task.WhenAll(
            firstService.AddOrUpdateAsync(
                new VocabularyModel { Word = " Apple " },
                new VocabularyMeaningModel
                {
                    BookId = "concurrency-book",
                    PartOfSpeech = " N. ",
                    Meaning = " fruit "
                }),
            secondService.AddOrUpdateAsync(
                new VocabularyModel { Word = "APPLE" },
                new VocabularyMeaningModel
                {
                    BookId = "concurrency-book",
                    PartOfSpeech = "n.",
                    Meaning = "fruit"
                }));

        Assert.Equal(results[0].word.Id, results[1].word.Id);
        Assert.Equal(results[0].meaning.Id, results[1].meaning.Id);

        await using var verificationContext = CreateContext(databasePath);
        Assert.Equal(1, await verificationContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
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
        var options = new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        return new VocabularyDbContext(options);
    }
}
