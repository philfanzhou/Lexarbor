using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class VocabularyRelationTests : TestBase
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    private static VocabularyRelationService Service(VocabularyDbContext db) =>
        new(new VocabularyRelationRepository(db), new UnitOfWork(db), TimeProvider.System);
    private static VocabularyRelationInput Input(string source = "write", string target = "wrote",
        string type = "past_tense", bool confirm = false, string? note = null) => new(source, target, type, note, confirm);

    internal static async Task SeedAsync(VocabularyDbContext db, CancellationToken token)
    {
        foreach (var word in new[] { "write", "wrote", "written", "cut", "good", "well", "better", "learn", "learned", "learnt" })
            db.Vocabularies.Add(new VocabularyEntity { Id = word, Word = word });
        await db.SaveChangesAsync(token);
    }

    [Fact]
    public async Task RelationsAreWordLevel_Bidirectional_ManyToMany_AndSelfRelationsAreNotDuplicated()
    {
        await SeedAsync(_dbContext, Ct);
        var service = Service(_dbContext);
        var relation = (await service.CreateAsync(Input(confirm: true), Ct)).Relation;
        Assert.Equal("confirmed", relation.Status);
        Assert.Null(relation.ReviewReason);
        Assert.NotNull(relation.LastConfirmedAtUnixMs);
        Assert.Equal(relation, Assert.Single((await service.ListAsync("write", new(Direction: "outgoing"), Ct)).Items));
        Assert.Equal(relation, Assert.Single((await service.ListAsync("wrote", new(Direction: "incoming"), Ct)).Items));
        await service.CreateAsync(Input("cut", "cut"), Ct);
        await service.CreateAsync(Input("cut", "cut", "past_participle"), Ct);
        Assert.Equal(2, (await service.ListAsync("cut", new(), Ct)).TotalCount);
        await service.CreateAsync(Input("good", "better", "comparative"), Ct);
        await service.CreateAsync(Input("well", "better", "comparative"), Ct);
        Assert.Equal(2, (await service.ListAsync("better", new(), Ct)).TotalCount);
        await service.CreateAsync(Input("learn", "learned"), Ct);
        await service.CreateAsync(Input("learn", "learnt"), Ct);
        Assert.Equal(2, (await service.ListAsync("learn", new(), Ct)).TotalCount);
        Assert.Empty(await _dbContext.VocabularyMeanings.ToListAsync(Ct));
    }

    [Fact]
    public async Task ReplaysNeverConfirmOrOverwrite_MetadataAndVersionAreProtected()
    {
        await SeedAsync(_dbContext, Ct);
        var service = Service(_dbContext);
        var batch = await service.CreateBatchAsync([Input(note: " note ")], Ct);
        var original = Assert.Single(batch.Items);
        Assert.Equal("import", original.OriginKind);
        Assert.NotNull(original.OriginReference);
        Assert.Equal("needs_review", original.Status);
        var replay = await service.CreateAsync(Input(confirm: true, note: "note"), Ct);
        Assert.False(replay.Created);
        Assert.Equal(original, replay.Relation);
        await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(Input(note: "different"), Ct));
        var confirmed = await service.ReplaceAsync(original.Id, Input(confirm: true, note: "note"), 1, Ct);
        Assert.Equal(2, confirmed.Version);
        Assert.Equal("confirmed", confirmed.Status);
        Assert.Equal(original.OriginReference, confirmed.OriginReference);
        await Assert.ThrowsAsync<ConflictException>(() => service.ReplaceAsync(original.Id, Input(), 1, Ct));
        await Assert.ThrowsAsync<ConflictException>(() => service.DeleteAsync(original.Id, 1, Ct));
        var edited = await service.ReplaceAsync(original.Id, Input(note: "different"), 2, Ct);
        Assert.Equal("relation_changed", edited.ReviewReason);
        Assert.Equal(confirmed.LastConfirmedAtUnixMs, edited.LastConfirmedAtUnixMs);
        await service.DeleteAsync(original.Id, 3, Ct);
        await service.DeleteAsync(original.Id, 3, Ct);
        Assert.True(await _dbContext.Vocabularies.AnyAsync(w => w.Id == "write", Ct));
        Assert.True(await _dbContext.Vocabularies.AnyAsync(w => w.Id == "wrote", Ct));
    }

    [Fact]
    public async Task BatchAndCollisionFailuresRollBack_QueriesValidateAndPage()
    {
        await SeedAsync(_dbContext, Ct);
        var service = Service(_dbContext);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.CreateBatchAsync([Input(), Input(target: "missing")], Ct));
        Assert.Empty(await _dbContext.VocabularyRelations.AsNoTracking().ToListAsync(Ct));
        var batch = await service.CreateBatchAsync([Input(), Input(), Input(target: "written", type: "past_participle")], Ct);
        Assert.Equal((2, 1), (batch.Created, batch.Reused));
        Assert.Equal(batch.Items[0].Id, batch.Items[1].Id);
        await Assert.ThrowsAsync<ConflictException>(() => service.ReplaceAsync(batch.Items[2].Id, Input(), 1, Ct));
        var page = await service.ListAsync("write", new(Size: 1), Ct);
        Assert.Equal((2, 2, 1), (page.TotalCount, page.TotalPage, page.Items.Count));
        Assert.Single((await service.ListAsync(null, new(Type: "past_participle", Status: "needs_review"), Ct)).Items);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ListAsync(null, new(Direction: "invalid"), Ct));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ListAsync(null, new(Page: int.MaxValue), Ct));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateBatchAsync([], Ct));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateAsync(Input(type: "Past_Tense"), Ct));
    }

    [Fact]
    public async Task WordRenameInvalidatesBothEndsAndSelfOnce_AndFailureRollsBackWordAndRelations()
    {
        await SeedAsync(_dbContext, Ct);
        var service = Service(_dbContext);
        var outgoing = (await service.CreateAsync(Input(confirm: true), Ct)).Relation;
        var incoming = (await service.CreateAsync(Input("cut", "write", confirm: true), Ct)).Relation;
        var self = (await service.CreateAsync(Input("write", "write", "past_participle", true), Ct)).Relation;
        var edit = new VocabularyWordEditService(_vocabularyRepository, new VocabularyWordEditRepository(_dbContext),
            _meaningRepository, _meaningUnitRepository, _unitOfWork);
        await edit.ReplaceAsync("write", " WRITE ", "phonetic", null, Ct);
        Assert.Equal(outgoing, await service.GetAsync(outgoing.Id, Ct));
        await edit.ReplaceAsync("write", "rewrite", null, null, Ct);
        foreach (var original in new[] { outgoing, incoming, self })
        {
            var changed = await service.GetAsync(original.Id, Ct);
            Assert.Equal(("needs_review", "word_changed", 2L), (changed.Status, changed.ReviewReason, changed.Version));
            Assert.Equal(original.LastConfirmedAtUnixMs, changed.LastConfirmedAtUnixMs);
        }
        await Assert.ThrowsAsync<ConflictException>(() => service.ReplaceAsync(outgoing.Id, Input(confirm: true), 1, Ct));
        await _dbContext.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_relation BEFORE UPDATE ON vocabulary_relation BEGIN SELECT RAISE(ABORT,'synthetic'); END;", Ct);
        await Assert.ThrowsAsync<ConflictException>(() => edit.ReplaceAsync("write", "another", null, null, Ct));
        Assert.Equal("rewrite", (await _dbContext.Vocabularies.AsNoTracking().SingleAsync(w => w.Id == "write", Ct)).Word);
        Assert.Equal(2, (await service.GetAsync(self.Id, Ct)).Version);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("self")]
    public async Task CleanupPreservesReferencedOrphansIncludingPendingRelations(string direction)
    {
        await SeedAsync(_dbContext, Ct);
        _dbContext.VocabularyBooks.Add(new VocabularyBookEntity { Id = "A", BookName = "A", Status = true });
        _dbContext.VocabularyMeanings.Add(new VocabularyMeaningEntity { Id = "m", VocabularyId = "write", BookId = "A", Meaning = "write" });
        await _dbContext.SaveChangesAsync(Ct);
        var relation = (await Service(_dbContext).CreateAsync(direction switch
        {
            "source" => Input(),
            "target" => Input("wrote", "write"),
            _ => Input("write", "write")
        }, Ct)).Relation;
        var cleanup = new VocabularyCleanupService(new VocabularyCleanupRepository(_dbContext), _unitOfWork);
        var preview = await cleanup.PreviewAsync("A", new("clear"), Ct);
        Assert.Equal((1, 1, 0), (preview.OrphanWordCount, preview.RetainedRelatedWordCount, preview.DeletableWordCount));
        var result = await cleanup.CommitAsync("A", new("clear"), Ct);
        Assert.Equal((1, 0, 1), (result.DeletedMeaningCount, result.DeletedWordCount, result.RetainedRelatedWordCount));
        await Service(_dbContext).DeleteAsync(relation.Id, 1, Ct);
        Assert.True(await _dbContext.Vocabularies.AnyAsync(w => w.Id == "write", Ct));
    }

    [Fact]
    public async Task SeparateContextsSerializeDuplicateCreatesAndRejectOneConcurrentStaleUpdate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexarbor-relations-{Guid.NewGuid():N}.db");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var options = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var first = new VocabularyDbContext(options);
            await first.Database.EnsureCreatedAsync(Ct);
            await SeedAsync(first, Ct);
            await using var second = new VocabularyDbContext(options);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstCreate = new UnitOfWork(first).ExecuteInTransactionAsync(async () =>
            {
                var result = await Service(first).CreateAsync(Input(), Ct);
                entered.SetResult();
                await release.Task;
                return result;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            var secondCreate = Service(second).CreateAsync(Input(), Ct);
            try { Assert.False(secondCreate.IsCompleted); }
            finally { release.TrySetResult(); }
            var created = await Task.WhenAll(firstCreate, secondCreate).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Single(created, r => r.Created);
            Assert.Equal(created[0].Relation.Id, created[1].Relation.Id);
            async Task<bool> Edit(VocabularyDbContext context, string note)
            {
                try { await Service(context).ReplaceAsync(created[0].Relation.Id, Input(note: note), 1, Ct); return true; }
                catch (ConflictException) { return false; }
            }
            var changed = await Task.WhenAll(Edit(first, "first"), Edit(second, "second"));
            Assert.Single(changed, success => success);
            Assert.Equal(2, (await Service(first).GetAsync(created[0].Relation.Id, Ct)).Version);
        }
        finally
        {
            release.TrySetResult();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Fact]
    public async Task CancellationWhileQueuedDoesNotCreateARelation()
    {
        await SeedAsync(_dbContext, Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
            return 0;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        try
        {
            var waiting = Service(_dbContext).CreateAsync(Input(), cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            release.TrySetResult();
            await hold.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Empty(await _dbContext.VocabularyRelations.AsNoTracking().ToListAsync(Ct));
        }
        finally { release.TrySetResult(); }
    }
}
