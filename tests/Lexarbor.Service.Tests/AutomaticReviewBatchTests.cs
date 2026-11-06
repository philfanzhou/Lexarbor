using System.Text.Json;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Service.Tests;

public sealed partial class AutomaticMeaningSubmissionReviewTests
{
    [Fact]
    public async Task BatchReviewProcessesSixSubmissionsInOneRequestAndDrainsRemainder()
    {
        await using var f = await Fixture.Create();
        for (var i = 0; i < 7; i++) await f.Submit("word" + i);
        f.Http.Respond = (_, _) => Task.FromResult(Completion(BatchDecisions(f)));
        Assert.Equal(6, await f.BatchWorker().RunAsync(Ct)); Assert.Equal(1, f.Http.Calls);
        Assert.Equal(1, (await f.Status()).PendingCount);
        f.Http.Respond = (_, _) => Task.FromResult(Completion(Decision()));
        Assert.Equal(1, await f.BatchWorker().RunAsync(Ct)); Assert.Equal(2, f.Http.Calls);
        Assert.Equal(0, (await f.Status()).PendingCount);
        await using var db = f.Open();
        Assert.Equal(7, await db.MeaningSubmissionAiReviews.CountAsync(Ct));
        Assert.Equal(2, await db.AiRequestBatches.CountAsync(Ct)); Assert.Equal(7, await db.AiRequestBatchMembers.CountAsync(Ct));
    }
    [Fact]
    public async Task BatchReviewPersistsAllValidResultsBeforePublicationAndRestartReusesThem()
    {
        await using var f = await Fixture.Create(); await f.Submit("first"); await f.Submit("second");
        f.Http.Respond = (_, _) => Task.FromResult(Completion(BatchDecisions(f)));
        f.Fault.Enabled = true;
        Assert.Equal(2, await f.BatchWorker().RunAsync(Ct));
        await using (var db = f.Open()) Assert.Equal(2, await db.MeaningSubmissionAutoReviews.CountAsync(r => r.ValidatedReviewJson != null, Ct));
        f.Fault.Enabled = false; f.Clock.Now += 8001;
        Assert.Equal(2, await f.BatchWorker().RunAsync(Ct));
        Assert.Equal(1, f.Http.Calls); Assert.Equal(0, (await f.Status()).PendingCount);
    }
    [Fact]
    public async Task BatchReviewPartialResponseOnlyRetriesMissingSubmission()
    {
        await using var f = await Fixture.Create(); await f.Submit("first"); await f.Submit("second");
        f.Http.Respond = (_, _) => Task.FromResult(Completion(BatchDecisions(f, 1)));
        Assert.Equal(2, await f.BatchWorker().RunAsync(Ct));
        Assert.Equal(1, (await f.Status()).PendingCount);
        Assert.Equal(0, await f.BatchWorker().RunAsync(Ct));
        f.Clock.Now += 8001;
        f.Http.Respond = (_, _) => Task.FromResult(Completion(Decision()));
        Assert.Equal(1, await f.BatchWorker().RunAsync(Ct));
        Assert.Equal(2, f.Http.Calls); Assert.Equal(0, (await f.Status()).PendingCount);
        await using var db = f.Open(); Assert.Equal(2, await db.MeaningSubmissionAiReviews.CountAsync(Ct));
    }
    [Fact]
    public async Task BatchReviewManualDecisionWinsWithoutDiscardingSiblingOrLateHistory()
    {
        await using var f = await Fixture.Create(); await f.Submit("first"); await f.Submit("second");
        f.Http.Respond = async (_, _) =>
        {
            await using var db = f.Open(); await f.Service(db).Submissions.RejectAsync(f.Ids["first"], "Manual wins", Ct);
            return Completion(BatchDecisions(f));
        };
        Assert.Equal(2, await f.BatchWorker().RunAsync(Ct));
        Assert.Equal("Manual wins", (await f.Read("first")).ReviewComment);
        Assert.Equal(MeaningSubmissionStatus.Approved, (await f.Read("second")).Status);
        await using var verify = f.Open();
        Assert.Equal(2, await verify.MeaningSubmissionAiReviews.CountAsync(Ct));
        Assert.Equal(1, await verify.MeaningSubmissionAiReviews.CountAsync(r => r.Applied, Ct));
    }

    [Fact]
    public async Task BatchReviewCancellationDuringPublicationRetainsEveryValidatedConclusionWithoutApplying()
    {
        await using var f = await Fixture.Create(); await f.Submit("first"); await f.Submit("second");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        f.Fault.Cancel = cancelled;
        f.Http.Respond = (_, _) => Task.FromResult(Completion(BatchDecisions(f)));
        Assert.Equal(2, await f.BatchWorker().RunAsync(cancelled.Token));
        Assert.True(f.Fault.Triggered); Assert.Equal(1, f.Http.Calls);
        await using var db = f.Open();
        Assert.Equal(2, await db.MeaningSubmissionAiReviews.CountAsync(Ct));
        Assert.Equal(0, await db.MeaningSubmissionAiReviews.CountAsync(r => r.Applied, Ct));
        Assert.Equal(2, (await f.Status()).PendingCount);
    }

    private static string BatchDecisions(Fixture f, int count = 6) => JsonSerializer.Serialize(new
    {
        results = f.Http.Proposals.Last().GetProperty("items").EnumerateArray().Reverse().Take(count).Select(item => new
        { id = item.GetProperty("id").GetString(), result = JsonSerializer.Deserialize<JsonElement>(Decision()) })
    });
}
