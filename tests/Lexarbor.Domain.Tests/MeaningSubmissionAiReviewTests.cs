using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class MeaningSubmissionAiReviewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lexarbor-review-").FullName;
    private string DatabasePath => Path.Combine(_root, "review.db");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Reviewed = DateTimeOffset.FromUnixTimeMilliseconds(1793577000123);
    private static MeaningSubmissionAiReview Review(string id, string verdict = "accept", string? attempt = null) =>
        new(attempt ?? Guid.NewGuid().ToString(), id, verdict, "v.", "原释义", "/ai-uk/", "/ai-us/", "合成说明", "fixture-model-actual", Reviewed);

    [Theory]
    [InlineData("accept", true)]
    [InlineData("revise", false)]
    [InlineData("reject", false)]
    public async Task Conclusions_Replay_Restart_CatalogChanges_NeverRewriteHistory(string verdict, bool kept)
    {
        string id; MeaningSubmissionAiReviewRecord saved;
        await using (var engine = await Open())
        {
            var pending = await engine.Service.SubmitAsync("source", "Word", "n.", "原释义", "original example", Ct);
            id = pending.Id; var review = Review(id, verdict);
            saved = await engine.Service.ApplyAiReviewAsync(review, new Eligibility(), Ct);
            Assert.True(saved.Applied);
            Assert.Equal(saved, await engine.Service.ApplyAiReviewAsync(review, new Eligibility(false), Ct));
            await Assert.ThrowsAsync<ConflictException>(() => engine.Service.ApplyAiReviewAsync(review with { Model = "other" }, new Eligibility(), Ct));
            var result = Assert.Single(await engine.Service.LookupAsync([id], Ct));
            Assert.Equal("Word", result.Word); Assert.Equal("n.", result.PartOfSpeech);
            Assert.Equal("原释义", result.Meaning); Assert.Equal("original example", result.Example);
            Assert.Equal("ai", result.ReviewerKind); Assert.Equal(kept, result.DefinitionKept);
            Assert.Equal(verdict == "accept" ? null : "合成说明", result.ReviewComment);
            Assert.Equal(Reviewed, result.ReviewedAt);
            if (verdict == "accept")
                Assert.Equal(id, (await engine.Service.SubmitAsync("source", "Word", "n.", "原释义", null, Ct)).Id);
            else
            {
                await Assert.ThrowsAsync<ResourceNotFoundException>(() => engine.Service.CreateQuestionAsync(id, true, Ct));
                Assert.NotEqual(id, (await engine.Service.SubmitAsync("source", "Word", "n.", "原释义", null, Ct)).Id);
            }
            if (result.ResultWordId != null)
            {
                await engine.Db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET meaning='corrected later' WHERE id={0}", [result.ResultMeaningId!], Ct);
                await engine.Db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary WHERE id={0}", [result.ResultWordId], Ct);
            }
        }
        await using var reopened = await Open();
        var record = await reopened.Submissions.GetReviewByAttemptIdAsync(saved.Review.AttemptId, Ct);
        Assert.Equal(saved, record);
        Assert.Equal("ai", (await reopened.Submissions.GetByIdAsync(id, Ct))!.ReviewerKind);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => reopened.Db.Database.ExecuteSqlRawAsync("UPDATE meaning_submission_ai_review SET model='changed'", Ct));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => reopened.Db.Database.ExecuteSqlRawAsync("DELETE FROM meaning_submission_ai_review", Ct));
        var row = await reopened.Db.MeaningSubmissionAiReviews.SingleAsync(Ct);
        row.Comment = "cannot overwrite";
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.Db.SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData("n.", "/changed/")]
    [InlineData("v.", null)]
    public async Task Revise_OnlyPhoneticsOrPos_ExplicitlyDisablesReuseAndQuestions(string pos, string? ipa)
    {
        await using var engine = await Open();
        var pending = await engine.Service.SubmitAsync("source", "Word", "n.", "原释义", null, Ct);
        await engine.Service.ApplyAiReviewAsync(Review(pending.Id, "revise") with { PartOfSpeech = pos, PhoneticUk = ipa, PhoneticUs = null }, new Eligibility(), Ct);
        var result = (await engine.Submissions.GetByIdAsync(pending.Id, Ct))!;
        Assert.False(result.DefinitionKept);
        Assert.Null(await engine.Submissions.FindReusableAsync("word", "n.", "原释义", Ct));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => engine.Service.CreateQuestionAsync(pending.Id, false, Ct));
    }

    [Fact]
    public async Task Accept_ReadsLatestDefaultsInWriter_OnlyFillsEachBlankSide()
    {
        await using var engine = await Open();
        var pending = await engine.Service.SubmitAsync("source", "Word", "n.", "原释义", "original example", Ct);
        // A manual edit after the model call's input was captured must win.
        await engine.Vocabulary.AddOrUpdateAsync(new() { Word = "Word", PhoneticUk = "/manual/", PhoneticUs = " " },
            new() { BookId = "book", Meaning = "another meaning" });
        await engine.Service.ApplyAiReviewAsync(Review(pending.Id), new Eligibility(), Ct);
        engine.Db.ChangeTracker.Clear();
        var word = await engine.Db.Vocabularies.SingleAsync(Ct);
        Assert.Equal("/manual/", word.PhoneticUk); Assert.Equal("/ai-us/", word.PhoneticUs);
        var result = (await engine.Submissions.GetByIdAsync(pending.Id, Ct))!;
        Assert.Equal("n.", result.ResultPartOfSpeech); Assert.True(result.DefinitionKept);
        Assert.Equal("original example", (await engine.Db.VocabularyMeanings.SingleAsync(m => m.Id == result.ResultMeaningId, Ct)).Example);
        var pending2 = await engine.Service.SubmitAsync("source", "Word", "n.", "different", null, Ct);
        await engine.Db.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET phonetic_uk='',phonetic_us='/manual-us/'", Ct);
        await engine.Service.ApplyAiReviewAsync(Review(pending2.Id), new Eligibility(), Ct);
        Assert.Equal("/ai-uk/", (await engine.Db.Vocabularies.AsNoTracking().SingleAsync(Ct)).PhoneticUk);
        Assert.Equal("/manual-us/", (await engine.Db.Vocabularies.AsNoTracking().SingleAsync(Ct)).PhoneticUs);
    }

    [Fact]
    public async Task AdminFirst_AndExpiredEligibility_PreserveUnappliedConclusions()
    {
        await using var engine = await Open();
        var pending = await engine.Service.SubmitAsync("source", "Word", null, "原释义", null, Ct);
        await engine.Service.RejectAsync(pending.Id, "manual reason", Ct);
        var record = await engine.Service.ApplyAiReviewAsync(Review(pending.Id, "revise"), new Eligibility(), Ct);
        Assert.False(record.Applied);
        Assert.Equal(record, await engine.Service.ApplyAiReviewAsync(record.Review, new Eligibility(), Ct));
        var current = (await engine.Submissions.GetByIdAsync(pending.Id, Ct))!;
        Assert.Equal(MeaningSubmissionStatus.Rejected, current.Status); Assert.Equal("admin", current.ReviewerKind);
        Assert.Equal("manual reason", current.ReviewComment); Assert.Empty(await engine.Db.Vocabularies.ToListAsync(Ct));
        var next = await engine.Service.SubmitAsync("source", "Word", null, "原释义", null, Ct);
        var gate = new Eligibility(false);
        var late = await engine.Service.ApplyAiReviewAsync(Review(next.Id), gate, Ct);
        Assert.True(gate.InWriter); Assert.False(late.Applied);
        Assert.Equal(MeaningSubmissionStatus.Pending, (await engine.Submissions.GetByIdAsync(next.Id, Ct))!.Status);
        Assert.Empty(await engine.Db.Vocabularies.ToListAsync(Ct));
        Assert.Equal(2, await engine.Db.MeaningSubmissionAiReviews.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoWriters_OnlyFirstChangesCatalog_SecondConclusionStillRecorded(bool secondIsAdmin)
    {
        await using var first = await Open();
        var pending = await first.Service.SubmitAsync("source", "Word", null, "原释义", null, Ct);
        await using var second = await Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winning = first.Service.ApplyAiReviewAsync(Review(pending.Id), new Eligibility(true, async () => { entered.SetResult(); await release.Task; }), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var competing = secondIsAdmin
            ? RejectAsObject(second, pending.Id)
            : second.Service.ApplyAiReviewAsync(Review(pending.Id, "reject"), new Eligibility(), Ct).ContinueWith<object>(t => t.GetAwaiter().GetResult(), Ct);
        Assert.False(competing.IsCompleted);
        release.SetResult(); Assert.True((await winning).Applied);
        if (secondIsAdmin) await Assert.ThrowsAsync<ConflictException>(() => competing);
        else Assert.False(((MeaningSubmissionAiReviewRecord)await competing).Applied);
        Assert.Single(await second.Db.Vocabularies.AsNoTracking().ToListAsync(Ct));
        Assert.Single(await second.Db.VocabularyMeanings.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(secondIsAdmin ? 1 : 2, await second.Db.MeaningSubmissionAiReviews.CountAsync(Ct));
    }

    private static async Task<object> RejectAsObject(Engine engine, string id) => await engine.Service.RejectAsync(id, Ct);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterCatalogSave_RollsBackEveryOutcome(bool cancel)
    {
        string id;
        await using (var engine = await Open()) id = (await engine.Service.SubmitAsync("source", "Word", null, "原释义", null, Ct)).Id;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using (var failing = await Open(new FailRecord(cancel ? cts : null)))
        {
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failing.Service.ApplyAiReviewAsync(Review(id), new Eligibility(), cts.Token));
            else await Assert.ThrowsAsync<DbUpdateException>(() => failing.Service.ApplyAiReviewAsync(Review(id), new Eligibility(), Ct));
        }
        await using var reopened = await Open();
        Assert.Equal(MeaningSubmissionStatus.Pending, (await reopened.Submissions.GetByIdAsync(id, Ct))!.Status);
        Assert.Empty(await reopened.Db.Vocabularies.ToListAsync(Ct)); Assert.Empty(await reopened.Db.VocabularyMeanings.ToListAsync(Ct));
        Assert.Empty(await reopened.Db.MeaningSubmissionAiReviews.ToListAsync(Ct));
        Assert.True((await reopened.Service.ApplyAiReviewAsync(Review(id), new Eligibility(), Ct)).Applied);
    }

    [Fact]
    public async Task UnboundedOrInvalidResults_DoNotWriteAnything()
    {
        await using var engine = await Open();
        var id = (await engine.Service.SubmitAsync("source", "Word", null, "原释义", null, Ct)).Id;
        foreach (var invalid in new[] { Review(id) with { Verdict = "invalid" }, Review(id) with { Model = new string('x', 201) },
            Review(id) with { Comment = new string('x', 501) }, Review(id, "revise") with { Meaning = null }, Review(id, "reject") with { Comment = " " },
            Review(id) with { PhoneticUk = new string('x', 101) }, Review(id) with { PartOfSpeech = new string('x', 33) } })
            await Assert.ThrowsAsync<DomainValidationException>(() => engine.Service.ApplyAiReviewAsync(invalid, new Eligibility(), Ct));
        Assert.Empty(await engine.Db.MeaningSubmissionAiReviews.ToListAsync(Ct));
        Assert.Equal(MeaningSubmissionStatus.Pending, (await engine.Submissions.GetByIdAsync(id, Ct))!.Status);
    }

    private async Task<Engine> Open(params IInterceptor[] interceptors)
    {
        var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={DatabasePath};Pooling=False").AddInterceptors(interceptors).Options);
        await db.Database.MigrateAsync(Ct);
        if (!await db.VocabularyBooks.AnyAsync(Ct)) { db.VocabularyBooks.Add(new() { Id = "book", BookName = "Fixture", Status = true }); await db.SaveChangesAsync(Ct); }
        return new(db);
    }
    private sealed class Engine : IAsyncDisposable
    {
        public VocabularyDbContext Db { get; }
        public MeaningSubmissionRepository Submissions { get; }
        public MeaningSubmissionService Service { get; }
        public VocabularyDomainService Vocabulary { get; }
        public Engine(VocabularyDbContext db)
        {
            Db = db; Submissions = new(db); var words = new VocabularyRepository(db); var meanings = new VocabularyMeaningRepository(db);
            var settings = new MeaningSubmissionSettings { BookId = "book" }; var uow = new UnitOfWork(db);
            Vocabulary = new(words, new VocabularyBookRepository(db), meanings, new VocabularyBookUnitRepository(db), new VocabularyMeaningUnitRepository(db), uow, settings, Submissions);
            Service = new(Submissions, words, meanings, Vocabulary, uow, settings, TimeProvider.System);
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
    private sealed class Eligibility(bool allowed = true, Func<Task>? pause = null) : IMeaningSubmissionReviewEligibility
    {
        public bool InWriter { get; private set; }
        public async Task<bool> CanApplyAsync(string submissionId, string attemptId, CancellationToken cancellationToken)
        { InWriter = UnitOfWork.IsWriteInProgress; Assert.True(InWriter); if (pause != null) await pause(); return allowed; }
    }
    private sealed class FailRecord(CancellationTokenSource? cancel) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("INSERT INTO \"meaning_submission_ai_review\"", StringComparison.Ordinal))
            { if (cancel != null) { cancel.Cancel(); cancellationToken.ThrowIfCancellationRequested(); } throw new InvalidOperationException("Synthetic record failure."); }
            return ValueTask.FromResult(result);
        }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
