using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lexarbor.Service.Tests;

public sealed class AutomaticMeaningSubmissionReviewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Decision(string verdict = "accept", string? meaning = "corrected", string? pos = "v.", string? uk = "ai-uk", string? us = "ai-us", string? comment = "Synthetic explanation") =>
        JsonSerializer.Serialize(new { verdict, partOfSpeech = pos, meaning, phoneticUk = uk, phoneticUs = us, comment });
    private static HttpResponseMessage Completion(string decision) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = decision } } } })) };

    [Fact]
    public async Task DefaultOff_MissingConfiguration_ZeroAutomaticCalls_ManualAdviceStillWorks()
    {
        await using var f = await Fixture.Create(enabled: false);
        await f.Submit("first");
        Assert.Equal(0, await f.Worker.RunBatchAsync(Ct)); Assert.Equal(0, f.Http.Calls);
        Assert.False(f.Runtime.Current.ReviewAssistant.AutoReviewEnabled);
        using var scope = f.Provider.CreateScope();
        var manual = new OpenAiCompatibleReviewAssistant(f.Clients, f.Runtime);
        Assert.True(manual.IsEnabled);
        Assert.Equal("accept", (await manual.ReviewAsync(await f.Read("first"), Ct)).Verdict);
        await f.Publish(new() { ["MeaningSubmissions:Ai:AutoReviewEnabled"] = "true", ["MeaningSubmissions:Ai:Endpoint"] = "" });
        await f.Submit("second");
        Assert.Equal(0, await f.Worker.RunBatchAsync(Ct)); Assert.Equal(1, f.Http.Calls);
        var status = await f.Status(); Assert.True(status.Enabled); Assert.True(status.Paused);
        Assert.Equal("configuration_missing", status.PauseCode); Assert.Equal(2, status.PendingCount);
        await f.Publish(new() { ["MeaningSubmissions:Ai:Endpoint"] = "https://review.example.test/v1", ["MeaningSubmissions:Ai:Model"] = new string('m', 201), ["MeaningSubmissions:Ai:ApiKey"] = "" });
        Assert.Equal(0, await f.Worker.RunBatchAsync(Ct)); Assert.Equal("configuration_invalid", (await f.Status()).PauseCode);
        await f.Publish(new() { ["MeaningSubmissions:Ai:Model"] = "self-hosted-no-key" });
        await f.Worker.RunBatchAsync(Ct); Assert.Equal(0, (await f.Status()).PendingCount);
        Assert.All(f.Http.Authenticated.Skip(1), authenticated => Assert.False(authenticated));
    }

    [Theory]
    [InlineData("accept", "v.", "changed", true)]
    [InlineData("revise", "v.", "original", false)]
    [InlineData("revise", "n.", "original", false)]
    [InlineData("reject", null, null, false)]
    public async Task RealWorker_ThreeVerdicts_PreservesWordExample_CapturesModelAndPermanentHistory(string verdict, string? pos, string? meaning, bool kept)
    {
        await using var f = await Fixture.Create();
        var submittedWord = verdict == "reject" ? "Mispelled" : "Word";
        await using (var db = f.Open())
        {
            var service = f.Service(db);
            await service.Vocabulary.AddOrUpdateAsync(new() { Word = submittedWord, PhoneticUk = "existing-uk" }, new() { BookId = "book", Meaning = "another" });
        }
        var id = await f.Submit(submittedWord);
        f.Http.Respond = (_, _) => Task.FromResult(Completion(Decision(verdict, meaning, pos, comment: "Possible spelling: misspelled")));
        Assert.Equal(1, await f.Worker.RunBatchAsync(Ct));
        var result = await f.Read(submittedWord);
        Assert.Equal(submittedWord, result.Word); Assert.Equal("original", result.Meaning); Assert.Equal("n.", result.PartOfSpeech);
        Assert.Equal("original example", result.Example); Assert.Equal(kept, result.DefinitionKept);
        Assert.Equal(verdict == "reject" ? MeaningSubmissionStatus.Rejected : MeaningSubmissionStatus.Approved, result.Status);
        Assert.Equal(verdict == "accept" ? null : "Possible spelling: misspelled", result.ReviewComment);
        Assert.Equal("ai", result.ReviewerKind);
        await using var reopened = f.Open();
        var record = await reopened.MeaningSubmissionAiReviews.SingleAsync(Ct);
        Assert.True(record.Applied); Assert.Equal("actual-model", record.Model); Assert.Equal(id, record.SubmissionId);
        Assert.Equal(f.Clock.Now, record.ReviewedAtUnixMs); Assert.Equal(verdict, record.Verdict);
        if (verdict != "reject")
        {
            var word = await reopened.Vocabularies.SingleAsync(Ct);
            Assert.Equal(verdict == "accept" ? "existing-uk" : "ai-uk", word.PhoneticUk); Assert.Equal("ai-us", word.PhoneticUs);
            Assert.Equal("original example", (await reopened.VocabularyMeanings.SingleAsync(m => m.Id == result.ResultMeaningId, Ct)).Example);
        }
        var request = Assert.Single(f.Http.Proposals);
        Assert.Equal(new[] { "example", "meaning", "partOfSpeech", "word" }, request.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Contains("Misspellings must always be rejected", Assert.Single(f.Http.Instructions));
        Assert.DoesNotContain("synthetic-source", request.ToString());
    }

    [Fact]
    public async Task Backlog_BoundedAndFair_IncludingLostNotifications_ContinuousFailuresAndRestart()
    {
        await using var f = await Fixture.Create(enabled: false);
        for (var i = 0; i < 25; i++) await f.Submit("word" + i.ToString("D2"));
        await f.Submit("broken"); f.Notifications.Drain();
        f.Http.Respond = (word, _) => Task.FromResult(word == "broken" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Completion(Decision()));
        await f.Publish(new() { ["MeaningSubmissions:Ai:AutoReviewEnabled"] = "true" });
        Assert.Equal(20, await f.Worker.RunBatchAsync(Ct)); Assert.Equal(20, f.Http.Calls);
        await f.NewWorker().RunBatchAsync(Ct); Assert.Equal(1, (await f.Status()).PendingCount);
        for (var i = 1; i <= 12; i++)
        {
            await using var read = f.Open();
            var row = await read.MeaningSubmissionAutoReviews.SingleAsync(s => s.SubmissionId == f.Ids["broken"], Ct);
            Assert.Equal(i, row.ConsecutiveFailures);
            Assert.Equal(Math.Min(120_000L, 8_000L << Math.Min(4, i - 1)), row.NextAttemptAtUnixMs - f.Clock.Now);
            Assert.Equal("service_unavailable", row.LastFailureCode);
            Assert.Equal(MeaningSubmissionStatus.Pending, (await f.Read("broken")).Status);
            Assert.Equal(0, await f.NewWorker().RunBatchAsync(Ct));
            f.Clock.Now = row.NextAttemptAtUnixMs;
            if (i < 12) await f.NewWorker().RunBatchAsync(Ct);
        }
        // Disabling does not consume attempts; new submissions join the same durable backlog.
        await f.Publish(new() { ["MeaningSubmissions:Ai:AutoReviewEnabled"] = "false" });
        await f.Submit("during-pause"); var calls = f.Http.Calls;
        Assert.Equal(0, await f.Worker.RunBatchAsync(Ct)); Assert.Equal(calls, f.Http.Calls);
        f.Http.Respond = (_, _) => Task.FromResult(Completion(Decision()));
        await f.Publish(new() { ["MeaningSubmissions:Ai:AutoReviewEnabled"] = "true" });
        await f.NewWorker().RunBatchAsync(Ct);
        Assert.Equal(0, (await f.Status()).PendingCount);
        await using var final = f.Open(); Assert.Equal(27, await final.MeaningSubmissionAiReviews.CountAsync(Ct));
        Assert.Equal(27, await final.VocabularyMeanings.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfter_PersistsLongerDelay_WithoutSharedEnrichmentPause(bool date)
    {
        await using var f = await Fixture.Create(); await f.Submit("limited"); await f.Submit("other");
        f.Http.Respond = (word, _) =>
        {
            if (word != "limited") return Task.FromResult(Completion(Decision()));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = date ? new RetryConditionHeaderValue(f.Clock.GetUtcNow().AddMinutes(10)) : new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        };
        await f.Worker.RunBatchAsync(Ct);
        var state = await f.Status(); Assert.Equal(1, state.PendingCount); Assert.Equal("rate_limited", state.LastFailureCode);
        Assert.Equal(f.Clock.GetUtcNow().AddMinutes(10), state.NextAttemptAt);
        await using var db = f.Open(); Assert.Empty(await db.EnrichmentServices.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCompetition_OrExpiredReclaimedLease_RetainsValidLateUnappliedResult(bool reclaimed)
    {
        await using var f = await Fixture.Create(); await f.Submit("race");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.Respond = async (_, _) => { entered.TrySetResult(); await finish.Task.WaitAsync(Ct); return Completion(Decision("revise")); };
        var first = f.Worker.RunBatchAsync(Ct); await entered.Task.WaitAsync(Ct);
        if (reclaimed)
        {
            f.Clock.Now += 61_000;
            f.Http.Respond = (_, _) => Task.FromResult(Completion(Decision("accept")));
            await f.NewWorker().RunBatchAsync(Ct);
        }
        else
        {
            await using var db = f.Open(); await f.Service(db).Submissions.RejectAsync(f.Ids["race"], "Manual reason", Ct);
        }
        await f.Publish(new() { ["MeaningSubmissions:Ai:Model"] = "next-model" });
        finish.TrySetResult(); await first;
        await using var reopened = f.Open();
        var records = await reopened.MeaningSubmissionAiReviews.OrderBy(r => r.Applied).ToListAsync(Ct);
        var late = records.First(); Assert.False(late.Applied); Assert.Equal("actual-model", late.Model); Assert.Equal("revise", late.Verdict);
        Assert.Equal(reclaimed ? 2 : 1, records.Count); Assert.Equal(reclaimed ? 1 : 0, await reopened.VocabularyMeanings.CountAsync(Ct));
        Assert.Equal(reclaimed ? MeaningSubmissionStatus.Approved : MeaningSubmissionStatus.Rejected, (await f.Read("race")).Status);
    }

    [Fact]
    public async Task CapturesModelPerCall_ConfigurationChangeDoesNotRelabelAnswer()
    {
        await using var f = await Fixture.Create(); await f.Submit("capture");
        f.Http.Respond = async (_, _) => { await f.Publish(new() { ["MeaningSubmissions:Ai:Model"] = "future-model" }); return Completion(Decision()); };
        await f.Worker.RunBatchAsync(Ct);
        await using var db = f.Open(); Assert.Equal("actual-model", (await db.MeaningSubmissionAiReviews.SingleAsync(Ct)).Model);
    }

    [Fact]
    public async Task DisablingDuringRequest_CancelsWithoutFailure_ThenAutomaticallyResumes()
    {
        await using var f = await Fixture.Create(); await f.Submit("pause");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.Respond = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Completion(Decision()); };
        var run = f.Worker.RunBatchAsync(Ct); await entered.Task.WaitAsync(Ct);
        await f.Publish(new() { ["MeaningSubmissions:Ai:AutoReviewEnabled"] = "false" }); await run;
        await using (var db = f.Open())
        {
            Assert.Empty(await db.MeaningSubmissionAiReviews.ToListAsync(Ct));
            var state = await db.MeaningSubmissionAutoReviews.SingleAsync(Ct); Assert.Equal(0, state.ConsecutiveFailures); Assert.Null(state.Owner);
        }
        f.Http.Respond = (_, _) => Task.FromResult(Completion(Decision()));
        await f.Publish(new() { ["MeaningSubmissions:Ai:AutoReviewEnabled"] = "true" }); await f.NewWorker().RunBatchAsync(Ct);
        Assert.Equal(0, (await f.Status()).PendingCount);
    }

    [Fact]
    public async Task ValidBodyCompletedAsDisableArrives_IsRetainedUnappliedRatherThanDiscarded()
    {
        await using var f = await Fixture.Create(); await f.Submit("late-valid");
        f.Http.Respond = (_, _) =>
        {
            using var complete = Completion(Decision());
            var bytes = complete.Content.ReadAsByteArrayAsync(Ct).GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new EndCallbackStream(bytes,
                () => f.Publish(new() { ["MeaningSubmissions:Ai:AutoReviewEnabled"] = "false" })))
            });
        };
        await f.Worker.RunBatchAsync(Ct);
        await using var db = f.Open();
        Assert.False((await db.MeaningSubmissionAiReviews.SingleAsync(Ct)).Applied);
        Assert.Equal(MeaningSubmissionStatus.Pending, (await f.Read("late-valid")).Status);
        Assert.Empty(await db.Vocabularies.ToListAsync(Ct));
    }

    [Fact]
    public async Task CancellationDuringOutcomeCommit_RollsBackCatalog_AndRetainsValidUnappliedConclusion()
    {
        await using var f = await Fixture.Create(); await f.Submit("cancel-before-commit");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        f.Fault.Cancel = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Worker.RunBatchAsync(cancellation.Token));
        Assert.True(f.Fault.Triggered);
        await using var db = f.Open(); Assert.Empty(await db.Vocabularies.ToListAsync(Ct));
        Assert.False((await db.MeaningSubmissionAiReviews.SingleAsync(Ct)).Applied);
        Assert.Equal(MeaningSubmissionStatus.Pending, (await f.Read("cancel-before-commit")).Status);
        Assert.Equal(0, (await db.MeaningSubmissionAutoReviews.SingleAsync(Ct)).ConsecutiveFailures);
    }

    [Theory]
    [InlineData(302, "request_rejected")]
    [InlineData(401, "request_rejected")]
    [InlineData(500, "service_unavailable")]
    public async Task SupplierErrors_OnlyFixedCodes_NoBodyOrHeaderPersistence(int status, string code)
    {
        await using var f = await Fixture.Create(); await f.Submit("error");
        f.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent("Synthetic private supplier failure body") });
        await f.Worker.RunBatchAsync(Ct); Assert.Equal(code, (await f.Status()).LastFailureCode);
        await using var db = f.Open(); Assert.Empty(await db.MeaningSubmissionAiReviews.ToListAsync(Ct));
    }

    public static IEnumerable<object[]> InvalidDecisions()
    {
        yield return ["{}"]; yield return ["[]"]; yield return ["```json\n" + Decision() + "\n```"];
        yield return [Decision().Replace("\"accept\"", "\"unknown\"", StringComparison.Ordinal)];
        yield return [Decision().Replace("\"verdict\":\"accept\"", "\"verdict\":\"accept\",\"verdict\":\"reject\"", StringComparison.Ordinal)];
        yield return [Decision()[..^1] + ",\"word\":\"rewritten\"}"];
        yield return [Decision()[..^1] + ",\"example\":\"rewritten\"}"];
        yield return [Decision("revise", "")]; yield return [Decision("revise", null)]; yield return [Decision("reject", comment: null)];
        yield return [Decision("revise", comment: new string('c', 501))]; yield return [Decision(pos: new string('p', 33))];
        yield return [Decision(uk: new string('i', 101))]; yield return [Decision(us: new string('i', 101))]; yield return [Decision(meaning: new string('m', 501))];
        yield return [Decision().Replace("\"partOfSpeech\":\"v.\"", "\"partOfSpeech\":42", StringComparison.Ordinal)];
        yield return [Decision().Replace("\"phoneticUk\":\"ai-uk\",", "", StringComparison.Ordinal)];
        yield return [Decision(comment: "synthetic-private-key").Replace("synthetic", "\\u0073ynthetic", StringComparison.Ordinal)];
        yield return [Decision(comment: "https://review.example.test/v1/private")];
        yield return ["{\"verdict\":\"accept\",\"meaning\":[[[[[[[[[null]]]]]]]]]}"];
    }

    [Theory]
    [MemberData(nameof(InvalidDecisions))]
    public async Task InvalidResponses_StayPending_OnlySafeCode_NoFabricatedConclusion(string decision)
    {
        await using var f = await Fixture.Create(); await f.Submit("invalid");
        f.Http.Respond = (_, _) => Task.FromResult(Completion(decision));
        await f.Worker.RunBatchAsync(Ct);
        var state = await f.Status(); Assert.Equal("invalid_response", state.LastFailureCode); Assert.Equal(1, state.PendingCount);
        await using var db = f.Open(); Assert.Empty(await db.MeaningSubmissionAiReviews.ToListAsync(Ct)); Assert.Empty(await db.Vocabularies.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BodyLimit_ChecksContentLengthAndStream_WithoutTruncating(bool knownLength)
    {
        await using var f = await Fixture.Create(); await f.Submit("large");
        f.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = knownLength ? new StringContent(new string('x', 65537)) : new StreamContent(new NonSeekStream(new byte[65537])) });
        await f.Worker.RunBatchAsync(Ct);
        Assert.Equal("response_too_large", (await f.Status()).LastFailureCode);
    }

    [Fact]
    public async Task ProductionNamedClient_DoesNotFollowRedirectsOrRetainSupplierCookies()
    {
        await using var supplier = WebApplication.Create(); supplier.Urls.Add("http://127.0.0.1:0");
        var followed = 0;
        supplier.Map("/start", context =>
        {
            context.Response.StatusCode = 302; context.Response.Headers.Location = "/followed";
            context.Response.Headers.SetCookie = "synthetic-cookie=private; Path=/"; return Task.CompletedTask;
        });
        supplier.Map("/followed", () => { Interlocked.Increment(ref followed); return Results.Ok(); });
        supplier.Map("/inspect", (HttpRequest request) => Results.Text(request.Headers.Cookie.ToString()));
        await supplier.StartAsync(Ct);
        try
        {
            using var factory = new VocabularyWebApplicationFactory();
            using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(AutomaticMeaningSubmissionReviewClient.HttpClientName);
            client.BaseAddress = new Uri(supplier.Urls.Single());
            using var redirect = await client.GetAsync("/start", Ct);
            Assert.Equal(HttpStatusCode.Found, redirect.StatusCode); Assert.Equal(0, followed);
            Assert.Equal("", await client.GetStringAsync("/inspect", Ct));
        }
        finally { await supplier.StopAsync(Ct); }
    }

    [Fact]
    public async Task TimeProvider_BodyAndHeadersShareBudget_CallerCancellationDoesNotBecomeFailure()
    {
        await using var f = await Fixture.Create();
        var submission = new MeaningSubmissionModel { Id = "synthetic", Word = "word", Meaning = "meaning" };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Http.Respond = (_, _) => { f.Clock.Now += 29_000; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(entered)) }); };
        var run = f.Client.ReviewAsync(submission, "attempt", f.Runtime.Current.ReviewAssistant, Ct);
        await entered.Task.WaitAsync(Ct); f.Clock.Advance(1000);
        Assert.Equal("service_timeout", (await run).FailureCode);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        f.Http.Respond = async (_, token) => { cancel.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Completion(Decision()); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Client.ReviewAsync(submission, "cancelled", f.Runtime.Current.ReviewAssistant, cancel.Token));
    }

    [Fact]
    public async Task HostedWorker_WakesOnCommit_RescansWithoutNotification_StopsCleanly_ImportNeverQueuesReview()
    {
        await using var f = await Fixture.Create();
        await using (var db = f.Open())
            await f.Service(db).Vocabulary.AddOrUpdateAsync(new() { Word = "imported" }, new() { BookId = "book", Meaning = "imported meaning" });
        await f.Worker.RunBatchAsync(Ct); Assert.Equal(0, f.Http.Calls);
        await f.Submit("existing-backlog"); f.Notifications.Drain();
        await f.Worker.StartAsync(Ct);
        await f.Http.Called.Task.WaitAsync(Ct);
        await f.WaitUntilReviewed("existing-backlog");
        await f.Submit("new-post-commit");
        await f.WaitUntilReviewed("new-post-commit");
        await f.Worker.StopAsync(Ct);
        Assert.Equal(2, f.Http.Calls); Assert.Equal(0, (await f.Status()).PendingCount);
    }

    [Fact]
    public async Task ValidatedConclusion_SurvivesApplicationFailureAndRestart_WithoutCallingSupplierAgain()
    {
        await using var f = await Fixture.Create(); await f.Submit("retained");
        f.Fault.Enabled = true;
        await f.Worker.RunBatchAsync(Ct);
        Assert.True(f.Fault.Triggered); Assert.Equal(1, f.Http.Calls);
        string staged; string attempt;
        await using (var db = f.Open())
        {
            Assert.Empty(await db.Vocabularies.ToListAsync(Ct)); Assert.Empty(await db.MeaningSubmissionAiReviews.ToListAsync(Ct));
            var state = await db.MeaningSubmissionAutoReviews.SingleAsync(Ct);
            staged = state.ValidatedReviewJson!; attempt = state.AttemptId;
            Assert.NotNull(staged); Assert.DoesNotContain("choices", staged); Assert.DoesNotContain("synthetic-private-key", staged);
            Assert.Equal("result_commit_failed", state.LastFailureCode); f.Clock.Now = state.NextAttemptAtUnixMs;
        }
        f.Fault.Enabled = false;
        await f.Publish(new() { ["MeaningSubmissions:Ai:Model"] = "changed-after-failure" });
        await f.NewWorker().RunBatchAsync(Ct);
        Assert.Equal(1, f.Http.Calls);
        await using var reopened = f.Open(); var record = await reopened.MeaningSubmissionAiReviews.SingleAsync(Ct);
        Assert.Equal(attempt, record.AttemptId); Assert.Equal("actual-model", record.Model); Assert.True(record.Applied);
        Assert.Null((await reopened.MeaningSubmissionAutoReviews.SingleAsync(Ct)).ValidatedReviewJson);
    }

    [Fact]
    public async Task StagedConclusion_StillPermanentlyRecordedAfterManualCompletionDuringCrash()
    {
        await using var f = await Fixture.Create(); var id = await f.Submit("manual-after-stage");
        f.Fault.Enabled = true; await f.Worker.RunBatchAsync(Ct); f.Fault.Enabled = false;
        await using (var db = f.Open())
        {
            await f.Service(db).Submissions.RejectAsync(id, "Manual wins", Ct);
            f.Clock.Now = (await db.MeaningSubmissionAutoReviews.AsNoTracking().SingleAsync(Ct)).NextAttemptAtUnixMs;
        }
        Assert.Equal(0, (await f.Status()).PendingCount);
        await f.NewWorker().RunBatchAsync(Ct);
        Assert.Equal(1, f.Http.Calls);
        await using var restarted = f.Open(); var record = await restarted.MeaningSubmissionAiReviews.SingleAsync(Ct);
        Assert.False(record.Applied); Assert.Equal("actual-model", record.Model);
        Assert.Null((await restarted.MeaningSubmissionAutoReviews.SingleAsync(Ct)).ValidatedReviewJson);
        Assert.Equal("Manual wins", (await f.Read("manual-after-stage")).ReviewComment);
    }

    [Fact]
    public async Task StaleOwnerCannotReleaseNewLease_EligibilityRequiresRealWriter_AndBoundaryValuesWork()
    {
        await using var f = await Fixture.Create(); var id = await f.Submit("lease");
        await using var first = f.Open(); var oldRepository = new MeaningSubmissionAutoReviewRepository(first, f.Clock);
        var old = (await oldRepository.ClaimAsync("old", TimeSpan.FromSeconds(1), Ct))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldRepository.IsCurrentAsync(old, Ct));
        f.Clock.Now += 1001;
        await using var second = f.Open(); var currentRepository = new MeaningSubmissionAutoReviewRepository(second, f.Clock);
        var current = (await currentRepository.ClaimAsync("new", TimeSpan.FromSeconds(60), Ct))!;
        Assert.NotEqual(old.AttemptId, current.AttemptId); Assert.True(current.Epoch > old.Epoch);
        await oldRepository.FinishAsync(old, "invalid_response", null, Ct);
        Assert.Equal("new", (await second.MeaningSubmissionAutoReviews.AsNoTracking().SingleAsync(Ct)).Owner);
        await currentRepository.FinishAsync(current, null, null, Ct);
        f.Http.Respond = (_, _) => Task.FromResult(Completion(Decision("revise", new string('m', 500), new string('p', 32), new string('i', 100), new string('u', 100), new string('c', 500))));
        await f.Worker.RunBatchAsync(Ct);
        var record = await second.MeaningSubmissionAiReviews.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(id, record.SubmissionId); Assert.True(record.Applied); Assert.Equal(500, record.Meaning!.Length);
    }

    [Fact]
    public async Task Migration_UpDownPreserveProposalsAndPermanentRecords_RetryStateSurvivesReopen()
    {
        var root = Directory.CreateTempSubdirectory("lexarbor-auto-migration-").FullName;
        var options = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={Path.Combine(root, "fixture.db")};Pooling=False").Options;
        try
        {
            await using (var db = new VocabularyDbContext(options))
            {
                var migrator = db.GetService<IMigrator>();
                await migrator.MigrateAsync("20261102043902_AddMeaningSubmissionReviews", Ct);
                await db.Database.ExecuteSqlRawAsync("INSERT INTO meaning_submission(id,source,word,meaning,status,created_at_unix_ms,updated_at_unix_ms) VALUES ('retained','synthetic','Word','original',0,1,2)", Ct);
                await db.Database.ExecuteSqlRawAsync("INSERT INTO meaning_submission_ai_review(id,attempt_id,submission_id,verdict,model,reviewed_at_unix_ms,applied) VALUES ('record','old-attempt','retained','reject','old-model',2,0)", Ct);
                await migrator.MigrateAsync(cancellationToken: Ct); Assert.False(db.Database.HasPendingModelChanges());
                Assert.Equal("original", (await db.MeaningSubmissions.SingleAsync(Ct)).Meaning);
                Assert.Equal("old-model", (await db.MeaningSubmissionAiReviews.SingleAsync(Ct)).Model);
                Assert.Empty(await db.MeaningSubmissionAutoReviews.ToListAsync(Ct));
                var repo = new MeaningSubmissionAutoReviewRepository(db, TimeProvider.System);
                var lease = (await repo.ClaimAsync("old-process", TimeSpan.FromSeconds(60), Ct))!;
                await repo.FinishAsync(lease, "rate_limited", TimeSpan.FromMinutes(10), Ct);
            }
            await using var restarted = new VocabularyDbContext(options);
            Assert.Equal("rate_limited", (await restarted.MeaningSubmissionAutoReviews.SingleAsync(Ct)).LastFailureCode);
            await restarted.GetService<IMigrator>().MigrateAsync("20261102043902_AddMeaningSubmissionReviews", Ct);
            Assert.Equal("original", (await restarted.MeaningSubmissions.SingleAsync(Ct)).Meaning);
            Assert.Equal("old-model", (await restarted.MeaningSubmissionAiReviews.SingleAsync(Ct)).Model);
            await restarted.Database.OpenConnectionAsync(Ct);
            using var command = restarted.Database.GetDbConnection().CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='meaning_submission_auto_review'";
            Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = VocabularyWebApplicationFactory.CreateGateSafeDirectory("auto-review-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _settings;
        private readonly List<IDisposable> _configurations = [];
        public Clock Clock { get; } = new(); public Handler Http { get; } = new();
        public FaultInterceptor Fault { get; } = new();
        public MeaningSubmissionReviewNotifications Notifications { get; } = new();
        public EnrichmentRuntimeConfiguration Runtime { get; }
        public ServiceProvider Provider { get; }
        public HttpFactory Clients { get; }
        public AutomaticMeaningSubmissionReviewClient Client { get; }
        public MeaningSubmissionReviewWorker Worker { get; }
        public Dictionary<string, string> Ids { get; } = [];
        private readonly MeaningSubmissionSettings _submissionSettings = new() { BookId = "book" };
        private Fixture(bool enabled)
        {
            _settings = new()
            {
                ["MeaningSubmissions:Ai:AutoReviewEnabled"] = enabled.ToString(),
                ["MeaningSubmissions:Ai:Endpoint"] = "https://review.example.test/v1",
                ["MeaningSubmissions:Ai:Model"] = "actual-model",
                ["MeaningSubmissions:Ai:ApiKey"] = "synthetic-private-key"
            };
            Runtime = new(Build(), new(_root)); Clients = new(Http); Client = new(Clients, Clock);
            var services = new ServiceCollection(); services.AddScoped(_ => Open());
            services.AddScoped<IMeaningSubmissionRepository>(p => new MeaningSubmissionRepository(p.GetRequiredService<VocabularyDbContext>()));
            services.AddScoped<IMeaningSubmissionAutoReviewRepository>(p => new MeaningSubmissionAutoReviewRepository(p.GetRequiredService<VocabularyDbContext>(), Clock));
            services.AddScoped(p => Service(p.GetRequiredService<VocabularyDbContext>()).Submissions);
            Provider = services.BuildServiceProvider(); Worker = NewWorker();
        }
        public static async Task<Fixture> Create(bool enabled = true)
        {
            var f = new Fixture(enabled); await using var db = f.Open(); await db.Database.MigrateAsync(Ct);
            db.VocabularyBooks.Add(new() { Id = "book", BookName = "Synthetic", Status = true }); await db.SaveChangesAsync(Ct); return f;
        }
        public MeaningSubmissionReviewWorker NewWorker() => new(Provider.GetRequiredService<IServiceScopeFactory>(), Runtime, _submissionSettings, Notifications, Clock, Client, NullLogger<MeaningSubmissionReviewWorker>.Instance);
        public VocabularyDbContext Open() => new(new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={Path.Combine(_root, "fixture.db")};Pooling=False").AddInterceptors(Fault).Options);
        public (MeaningSubmissionService Submissions, VocabularyDomainService Vocabulary) Service(VocabularyDbContext db)
        {
            var submissions = new MeaningSubmissionRepository(db); var words = new VocabularyRepository(db); var meanings = new VocabularyMeaningRepository(db); var uow = new UnitOfWork(db);
            var vocabulary = new VocabularyDomainService(words, new VocabularyBookRepository(db), meanings, new VocabularyBookUnitRepository(db), new VocabularyMeaningUnitRepository(db), uow, _submissionSettings, submissions);
            return (new(submissions, words, meanings, vocabulary, uow, _submissionSettings, Clock, changes: Notifications), vocabulary);
        }
        public async Task<string> Submit(string word)
        {
            await using var db = Open(); var submission = await Service(db).Submissions.SubmitAsync("synthetic-source", word, "n.", "original", "original example", Ct);
            Ids[word] = submission.Id; return submission.Id;
        }
        public async Task<MeaningSubmissionModel> Read(string word)
        { await using var db = Open(); return (await new MeaningSubmissionRepository(db).GetByIdAsync(Ids[word], Ct))!; }
        public async Task<MeaningSubmissionAutomaticReviewStatus> Status()
        { await using var db = Open(); return await new MeaningSubmissionAutomaticReviewStatusProvider(Runtime, _submissionSettings, new MeaningSubmissionAutoReviewRepository(db, Clock)).ReadAsync(Ct); }
        public async Task WaitUntilReviewed(string word)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while ((await Read(word)).Status == MeaningSubmissionStatus.Pending) await Task.Delay(10, timeout.Token);
        }
        private IConfigurationRoot Build() { var c = new ConfigurationBuilder().AddInMemoryCollection(_settings).Build(); _configurations.Add((IDisposable)c); return c; }
        public async Task Publish(Dictionary<string, string?> changes)
        { foreach (var pair in changes) _settings[pair.Key] = pair.Value; using var prepared = await Runtime.PrepareAsync(Build(), Ct); prepared!.Publish(); }
        public async ValueTask DisposeAsync()
        { Worker.Dispose(); await Provider.DisposeAsync(); Clients.Dispose(); foreach (var config in _configurations) config.Dispose(); Directory.Delete(_root, true); }
    }
    private sealed class FaultInterceptor : DbCommandInterceptor
    {
        public bool Enabled; public bool Triggered; public CancellationTokenSource? Cancel;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (Cancel != null && !Triggered && command.CommandText.StartsWith("INSERT INTO \"meaning_submission_ai_review\"", StringComparison.Ordinal))
            { Triggered = true; Cancel.Cancel(); token.ThrowIfCancellationRequested(); }
            if (Enabled && command.CommandText.StartsWith("INSERT INTO \"meaning_submission_ai_review\"", StringComparison.Ordinal))
            { Triggered = true; throw new InvalidOperationException("Synthetic commit failure."); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<bool> Authenticated { get; } = [];
        public int Calls; public List<JsonElement> Proposals { get; } = []; public List<string> Instructions { get; } = [];
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<string, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(Completion(Decision()));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls); Authenticated.Add(request.Headers.Authorization != null);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Instructions.Add(json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!);
            var proposal = JsonDocument.Parse(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            using (proposal) Proposals.Add(proposal.RootElement.Clone()); Called.TrySetResult();
            return await Respond(Proposals.Last().GetProperty("word").GetString()!, token);
        }
    }
    private sealed class HttpFactory(Handler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler, false) { Timeout = Timeout.InfiniteTimeSpan };
        public HttpClient CreateClient(string name) => _client; public void Dispose() => _client.Dispose();
    }
    private sealed class Clock : TimeProvider
    {
        public long Now { get; set; } = 1_793_577_000_000;
        private readonly List<Timer> _timers = [];
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(callback, state, Now + (long)dueTime.TotalMilliseconds); lock (_timers) _timers.Add(timer); return timer; }
        public void Advance(long milliseconds)
        { Now += milliseconds; Timer[] timers; lock (_timers) timers = _timers.ToArray(); foreach (var timer in timers) timer.Fire(Now); }
        private sealed class Timer(TimerCallback callback, object? state, long due) : ITimer
        {
            private int _disposed;
            public void Fire(long now) { if (now >= due && Interlocked.Exchange(ref _disposed, 1) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private class NonSeekStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    private sealed class EndCallbackStream(byte[] bytes, Func<Task> beforeEnd) : NonSeekStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position == Length) { await beforeEnd(); return 0; }
            return await base.ReadAsync(buffer, token);
        }
    }
    private sealed class SlowStream(TaskCompletionSource entered) : NonSeekStream([])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
    }
}
