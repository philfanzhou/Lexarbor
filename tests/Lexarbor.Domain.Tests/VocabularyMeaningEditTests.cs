using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public class VocabularyMeaningEditTests : TestBase
{
    private static VocabularyMeaningEditService Service(VocabularyDbContext db) => new(new VocabularyRepository(db),
        new VocabularyBookRepository(db), new VocabularyMeaningRepository(db), new UnitOfWork(db));

    [Theory]
    [InlineData(null, " ", true)]
    [InlineData(" ", null, false)]
    [InlineData(" N. ", " Example ", false)]
    public async Task Replace_OnlyTargetMeaning_AlsoInDisabledBooks(string? part, string? example, bool enabled)
    {
        await SeedAsync(_dbContext, enabled);
        var words = JsonSerializer.Serialize(await _dbContext.Vocabularies.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        var others = JsonSerializer.Serialize(await _dbContext.VocabularyMeanings.AsNoTracking().Where(m => m.Id != "a").ToListAsync(TestContext.Current.CancellationToken));
        await Service(_dbContext).ReplaceAsync("A", "w", "a", part, " Meaning ", example, TestContext.Current.CancellationToken);
        var current = await _dbContext.VocabularyMeanings.AsNoTracking().SingleAsync(m => m.Id == "a", TestContext.Current.CancellationToken);
        Assert.Equal("Meaning", current.Meaning);
        Assert.Equal(string.IsNullOrWhiteSpace(part) ? "" : "n.", current.PartOfSpeech);
        Assert.Equal(string.IsNullOrWhiteSpace(example) ? null : "Example", current.Example);
        Assert.Equal("A", current.BookId);
        Assert.Equal("w", current.VocabularyId);
        Assert.True(current.UpdatedAt > current.CreatedAt);
        Assert.Equal(words, JsonSerializer.Serialize(await _dbContext.Vocabularies.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(others, JsonSerializer.Serialize(await _dbContext.VocabularyMeanings.AsNoTracking().Where(m => m.Id != "a").ToListAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("missing", "w", "a", false)]
    [InlineData("A", "missing", "a", false)]
    [InlineData("A", "w", "missing", false)]
    [InlineData("B", "w", "a", true)]
    [InlineData("A", "other", "a", true)]
    public async Task ResourceAndOwnershipChecks_PrecedeWrites(string book, string word, string meaning, bool conflict)
    {
        await SeedAsync(_dbContext);
        Func<Task> action = () => Service(_dbContext).ReplaceAsync(book, word, meaning, null, "changed", null, TestContext.Current.CancellationToken);
        if (conflict) await Assert.ThrowsAsync<ConflictException>(action);
        else await Assert.ThrowsAsync<ResourceNotFoundException>(action);
        Assert.Equal("meaning-a", (await _dbContext.VocabularyMeanings.AsNoTracking().SingleAsync(m => m.Id == "a", TestContext.Current.CancellationToken)).Meaning);
    }

    [Fact]
    public async Task EquivalenceCancellationAndConstraintFailure_RollBack()
    {
        await SeedAsync(_dbContext);
        var before = JsonSerializer.Serialize(await _dbContext.VocabularyMeanings.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ConflictException>(() => Service(_dbContext).ReplaceAsync("A", "w", "a", " N. ", " meaning-other ", "changed", TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(_dbContext).ReplaceAsync("A", "w", "a", null, "changed", null, cancelled.Token));
        await _dbContext.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_meaning BEFORE UPDATE ON vocabulary_meaning BEGIN SELECT RAISE(ABORT,'synthetic'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ConflictException>(() => Service(_dbContext).ReplaceAsync("A", "w", "a", null, "changed", null, TestContext.Current.CancellationToken));
        Assert.Equal(before, JsonSerializer.Serialize(await _dbContext.VocabularyMeanings.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("edit", "edit")]
    [InlineData("import", "edit")]
    [InlineData("edit", "import")]
    public async Task IndependentContexts_SerializeEditsAndImports(string firstKind, string secondKind)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexarbor-meaning-edit-{Guid.NewGuid():N}.db");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var options = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={path};Pooling=False;Default Timeout=1").Options;
            await using var first = new VocabularyDbContext(options);
            await first.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            await first.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", TestContext.Current.CancellationToken);
            await SeedAsync(first);
            await using var second = new VocabularyDbContext(options);
            if (secondKind == "edit") await second.VocabularyMeanings.FindAsync(["a"], TestContext.Current.CancellationToken);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var disconnected = new CancellationTokenSource();
            var firstWrite = new UnitOfWork(first).ExecuteInTransactionAsync(async () =>
            {
                await WriteAsync(first, firstKind, "first", disconnected.Token);
                entered.SetResult(); await release.Task; return 0;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            var secondWrite = WriteAsync(second, secondKind, "second", TestContext.Current.CancellationToken);
            try { Assert.False(secondWrite.IsCompleted); disconnected.Cancel(); }
            finally { release.TrySetResult(); }
            await Task.WhenAll(firstWrite, secondWrite).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await using var verify = new VocabularyDbContext(options);
            var stored = await verify.VocabularyMeanings.SingleAsync(m => m.Id == "a", TestContext.Current.CancellationToken);
            Assert.Equal("second", stored.Example);
            Assert.Equal(3, await verify.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }

    private static async Task WriteAsync(VocabularyDbContext db, string kind, string value, CancellationToken cancellationToken)
    {
        if (kind == "edit") await Service(db).ReplaceAsync("A", "w", "a", "n.", "meaning-a", value, cancellationToken);
        else await new VocabularyDomainService(new VocabularyRepository(db), new VocabularyBookRepository(db),
            new VocabularyMeaningRepository(db), new UnitOfWork(db)).AddOrUpdateAsync(
            new VocabularyModel { Id = "w", Word = "shared" },
            new VocabularyMeaningModel { Id = "a", BookId = "A", VocabularyId = "w", PartOfSpeech = "n.", Meaning = "meaning-a", Example = value });
    }

    private static async Task SeedAsync(VocabularyDbContext db, bool enabled = true)
    {
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = enabled }, new VocabularyBookEntity { Id = "B", BookName = "B", Status = false });
        db.Vocabularies.AddRange(new VocabularyEntity { Id = "w", Word = "shared", PhoneticUk = "uk", PhoneticUs = "us" }, new VocabularyEntity { Id = "other", Word = "other" });
        db.VocabularyMeanings.AddRange(new VocabularyMeaningEntity { Id = "a", VocabularyId = "w", BookId = "A", PartOfSpeech = "n.", Meaning = "meaning-a", Example = "a" },
            new VocabularyMeaningEntity { Id = "a2", VocabularyId = "w", BookId = "A", PartOfSpeech = "n.", Meaning = "meaning-other", Example = "other" },
            new VocabularyMeaningEntity { Id = "b", VocabularyId = "w", BookId = "B", Meaning = "meaning-b", Example = "b" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
