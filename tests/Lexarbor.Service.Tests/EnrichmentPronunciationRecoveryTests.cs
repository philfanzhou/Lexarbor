using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Host;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    [Theory]
    [InlineData("tts.capability_unsupported", true)]
    [InlineData("tts.prepared_plan_required", true)]
    [InlineData("tts.service_unavailable", false)]
    public async Task AudioRetry_RefreshesOnlyLocallyRejectedPreparation(string error, bool reprepare)
    {
        await using var f = await Fixture.Create(fields: false);
        var prepared = (await f.Run(Key(EnrichmentStep.Prepare)))!;
        var audio = await f.Ensure(Key());
        var guard = (await f.Tasks.ClaimAsync(audio.Id, "fail", f.Clock.Now, f.Clock.Now + 10000, Ct))!;
        await f.Tasks.StopAsync(guard, error, f.Clock.Now, Ct);
        var c = await AdminConfig(f);
        Assert.Equal(EnrichmentRetryStatus.Started, (await f.Tasks.RetryAsync(audio.Id, audio.CurrentRound, Guid.NewGuid().ToString(), c, f.Clock.Now, Ct)).Status);
        Assert.Equal(prepared.CurrentRound + (reprepare ? 1 : 0), (await f.Tasks.GetAsync(prepared.Id, Ct))!.CurrentRound);
        var result = (await f.Executor.ExecuteAsync(audio.Id, "retry", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State);
        Assert.Equal(reprepare ? 2 : 1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls);
    }

    [Theory]
    [InlineData("text.phonetic_constraint_mismatch")]
    [InlineData("text.template_unexpanded")]
    [InlineData("text.pronunciation_control_unsupported")]
    public async Task PreparationDiagnostics_ArePreservedBySafeAdminProjection(string error)
    {
        await using var f = await Fixture.Create(fields: false);
        var task = await f.Ensure(Key(EnrichmentStep.Prepare));
        var guard = (await f.Tasks.ClaimAsync(task.Id, "fail", f.Clock.Now, f.Clock.Now + 10000, Ct))!;
        await f.Tasks.StopAsync(guard, error, f.Clock.Now, Ct);
        var detail = (await new EnrichmentAdminRepository(f.Db).DetailAsync(task.Id, 1, 10, await AdminConfig(f), f.Clock.Now, Ct))!;
        Assert.Equal(error, detail.Task.Current.ErrorCode); Assert.Equal(error, Assert.Single(detail.Rounds.Items).ErrorCode);
        var retried = await f.Tasks.RetryAsync(task.Id, task.CurrentRound, Guid.NewGuid().ToString(), await AdminConfig(f), f.Clock.Now, Ct);
        Assert.Equal(EnrichmentRetryStatus.Started, retried.Status);
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Executor.ExecuteAsync(task.Id, "retry", Ct))!).State);
    }

    [Fact]
    public async Task LegacyFailedPreparation_IsNotRevivedByUpgrade_ButExplicitRetryUsesNewCapabilities()
    {
        await using var f = await Fixture.Create(fields: false);
        var key = Key(EnrichmentStep.Prepare);
        var version = EnrichmentTargetExecutor.ConfigurationVersion(key, f.Runtime.Current);
        var legacy = (await f.Tasks.EnsureAsync(key, version.Split(":speech:")[0], f.Clock.Now, Ct))!;
        var guard = (await f.Tasks.ClaimAsync(legacy.Id, "legacy", f.Clock.Now, f.Clock.Now + 10000, Ct))!;
        await f.Tasks.StopAsync(guard, "text.invalid_plan", f.Clock.Now, Ct);
        var unchanged = await f.Ensure(key);
        Assert.Equal(legacy.CurrentRound, unchanged.CurrentRound); Assert.Equal(EnrichmentState.Failed, Round(unchanged).State);
        Assert.Equal(0, f.Http.TextCalls);
        await f.Tasks.RetryAsync(legacy.Id, legacy.CurrentRound, Guid.NewGuid().ToString(), await AdminConfig(f), f.Clock.Now, Ct);
        var completed = (await f.Executor.ExecuteAsync(legacy.Id, "retry", Ct))!;
        Assert.Equal(2, completed.CurrentRound); Assert.Equal(EnrichmentState.Succeeded, Round(completed).State);
        Assert.Equal(EnrichmentState.Failed, completed.Rounds[0].State);
    }

    [Fact]
    public async Task TtsCorrection_RefreshesFailedPreparationForOnlyItsOwnAccent()
    {
        await using var f = await Fixture.Create(fields: false, native: true, shared: true);
        var result = (await f.Run(Key()))!;
        var prepare = await f.Ensure(Key(EnrichmentStep.Prepare));
        Assert.Equal("text.pronunciation_control_unsupported", Round(prepare).ErrorCode);
        Assert.Equal("preparation.failed", Round(result).ErrorCode);
        Assert.Equal(0, f.Http.SpeechCalls);
        var usVersion = EnrichmentTargetExecutor.ConfigurationVersion(Key(EnrichmentStep.Prepare, AudioLocale.EnUs), f.Runtime.Current);
        f.Http.Ipa = true;
        await f.Publish(new() { ["Enrichment:Tts:Uk:Voice"] = "fixed-uk" });
        Assert.Equal(usVersion, EnrichmentTargetExecutor.ConfigurationVersion(Key(EnrichmentStep.Prepare, AudioLocale.EnUs), f.Runtime.Current));
        result = (await f.Run(Key()))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State); Assert.Equal(1, f.Http.SpeechCalls);
        var c = await AdminConfig(f);
        foreach (var locale in new[] { AudioLocale.EnGb, AudioLocale.EnUs })
            Assert.Equal(EnrichmentTargetExecutor.ConfigurationVersion(Key(EnrichmentStep.Prepare, locale), f.Runtime.Current), c.Version(Key(EnrichmentStep.Prepare, locale)));
    }
}
