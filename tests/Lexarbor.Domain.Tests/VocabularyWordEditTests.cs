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

public class VocabularyWordEditTests : TestBase
{
    private static VocabularyWordEditService Service(VocabularyDbContext db) =>
        new(new VocabularyRepository(db), new VocabularyWordEditRepository(db), new UnitOfWork(db));

    [Theory]
    [InlineData(null, " ")]
    [InlineData(" ", null)]
    [InlineData(" uk ", " us ")]
    public async Task Replace_UpdatesOnlySharedWordAndAllowsDisabledMembership(string? uk, string? us)
    {
        await SeedAsync(_dbContext);
        var meanings = JsonSerializer.Serialize(await _dbContext.VocabularyMeanings.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        var original = await _dbContext.Vocabularies.AsNoTracking().SingleAsync(v => v.Id == "w", TestContext.Current.CancellationToken);
        await Service(_dbContext).ReplaceAsync("w", " REPLACED ", uk, us, TestContext.Current.CancellationToken);
        var updated = await _dbContext.Vocabularies.AsNoTracking().SingleAsync(v => v.Id == "w", TestContext.Current.CancellationToken);
        Assert.Equal("replaced", updated.Word);
        Assert.Equal(string.IsNullOrWhiteSpace(uk) ? null : "uk", updated.PhoneticUk);
        Assert.Equal(string.IsNullOrWhiteSpace(us) ? null : "us", updated.PhoneticUs);
        Assert.Equal(original.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt > original.UpdatedAt);
        Assert.Equal(meanings, JsonSerializer.Serialize(await _dbContext.VocabularyMeanings.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)));
        await Service(_dbContext).ReplaceAsync("orphan", " historical ", null, null, TestContext.Current.CancellationToken);
        Assert.Equal("historical", (await _dbContext.Vocabularies.AsNoTracking().SingleAsync(v => v.Id == "orphan", TestContext.Current.CancellationToken)).Word);
    }

    [Fact]
    public async Task AnyOtherHistoricalNormalizedDuplicate_ConflictsWithoutPartialWrite()
    {
        await SeedAsync(_dbContext);
        _dbContext.Vocabularies.AddRange(new VocabularyEntity { Id = "duplicate1", Word = " Original " },
            new VocabularyEntity { Id = "duplicate2", Word = "ORIGINAL" });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ConflictException>(() => Service(_dbContext).ReplaceAsync("w", "original", null, null, TestContext.Current.CancellationToken));
        var current = await _dbContext.Vocabularies.AsNoTracking().SingleAsync(v => v.Id == "w", TestContext.Current.CancellationToken);
        Assert.Equal("old-uk", current.PhoneticUk);
        Assert.Equal("old-us", current.PhoneticUs);
    }

    [Fact]
    public async Task MissingInvalidCancelledAndConstraintFailure_DoNotPersistChanges()
    {
        await SeedAsync(_dbContext);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service(_dbContext).ReplaceAsync("missing", "new", null, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DomainValidationException>(() => Service(_dbContext).ReplaceAsync("w", " ", null, null, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(_dbContext).ReplaceAsync("w", "new", null, null, cancellation.Token));
        await _dbContext.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_word BEFORE UPDATE ON vocabulary BEGIN SELECT RAISE(ABORT,'synthetic'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ConflictException>(() => Service(_dbContext).ReplaceAsync("w", "new", null, null, TestContext.Current.CancellationToken));
        var persisted = await _dbContext.Vocabularies.AsNoTracking().SingleAsync(v => v.Id == "w", TestContext.Current.CancellationToken);
        Assert.Equal("original", persisted.Word);
        Assert.Equal("old-uk", persisted.PhoneticUk);
    }

    [Theory]
    [InlineData("edit", "edit")]
    [InlineData("import", "edit")]
    [InlineData("edit", "import")]
    public async Task IndependentContexts_SerializeEditsAndImports_AndCommitAfterCancellation(string firstKind, string secondKind)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexarbor-word-edit-{Guid.NewGuid():N}.db");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var options = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={path};Pooling=False;Default Timeout=1").Options;
            await using var first = new VocabularyDbContext(options);
            await first.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            await first.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", TestContext.Current.CancellationToken);
            await SeedAsync(first);
            await using var second = new VocabularyDbContext(options);
            // Populate a stale tracked snapshot before the other transaction.
            if (secondKind == "edit") await second.Vocabularies.FindAsync(["w"], TestContext.Current.CancellationToken);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var disconnected = new CancellationTokenSource();
            var firstWrite = new UnitOfWork(first).ExecuteInTransactionAsync(async () =>
            {
                await WriteAsync(first, firstKind, "first", disconnected.Token);
                entered.SetResult();
                await release.Task;
                return 0;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            var secondWrite = WriteAsync(second, secondKind, "second", TestContext.Current.CancellationToken);
            try
            {
                Assert.False(secondWrite.IsCompleted);
                disconnected.Cancel(); // the first write is already inside its transaction
            }
            finally { release.TrySetResult(); }
            await Task.WhenAll(firstWrite, secondWrite).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await using var verify = new VocabularyDbContext(options);
            var stored = await verify.Vocabularies.SingleAsync(v => v.Id == "w", TestContext.Current.CancellationToken);
            Assert.Equal("second", stored.PhoneticUk);
            Assert.Null(stored.PhoneticUs);
            Assert.Equal(2, await verify.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            release.TrySetResult();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private static async Task WriteAsync(VocabularyDbContext db, string kind, string value, CancellationToken cancellationToken)
    {
        if (kind == "edit") await Service(db).ReplaceAsync("w", "original", value, null, cancellationToken);
        else await new VocabularyDomainService(new VocabularyRepository(db), new VocabularyBookRepository(db),
            new VocabularyMeaningRepository(db), new UnitOfWork(db)).AddOrUpdateAsync(
            new VocabularyModel { Id = "w", Word = "original", PhoneticUk = value },
            new VocabularyMeaningModel { Id = "a", BookId = "A", VocabularyId = "w", Meaning = "meaning-a" });
    }

    private static async Task SeedAsync(VocabularyDbContext db)
    {
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = true }, new VocabularyBookEntity { Id = "B", BookName = "B", Status = false });
        db.Vocabularies.AddRange(new VocabularyEntity { Id = "w", Word = "original", PhoneticUk = "old-uk", PhoneticUs = "old-us" }, new VocabularyEntity { Id = "orphan", Word = "orphan" });
        db.VocabularyMeanings.AddRange(new VocabularyMeaningEntity { Id = "a", VocabularyId = "w", BookId = "A", Meaning = "meaning-a", Example = "a" },
            new VocabularyMeaningEntity { Id = "b", VocabularyId = "w", BookId = "B", Meaning = "meaning-b", Example = "b" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
