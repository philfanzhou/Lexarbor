using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    private static async Task FailLocally(Fixture f, EnrichmentTask task, string error)
    {
        var guard = (await f.Tasks.ClaimAsync(task.Id, "legacy", f.Clock.Now, f.Clock.Now + 10000, Ct))!;
        await f.Tasks.StopAsync(guard, error, f.Clock.Now, Ct);
    }

    [Fact]
    public async Task AutomaticRecovery_ConcurrentCoordinatorsCreateOnlyOneRound_AndWakeDependencies()
    {
        await using var f = await Fixture.Create();
        await f.Publish(new() { ["Enrichment:FailureBudget"] = "20" });
        var prepare = await f.Ensure(Key(EnrichmentStep.Prepare));
        await FailLocally(f, prepare, "text.invalid_plan");
        var fields = (await f.Run(Key(EnrichmentStep.Fields)))!;
        var audio = (await f.Run(Key()))!;
        Assert.Equal("preparation.failed", Round(fields).ErrorCode);
        Assert.Equal("preparation.failed", Round(audio).ErrorCode);
        var config = await AdminConfig(f);
        var admin = new EnrichmentAdminRepository(f.Db);
        Assert.True((await admin.DetailAsync(prepare.Id, 1, 10, config, f.Clock.Now, Ct))!.Task.AutomaticRecoveryAvailable);
        Assert.True((await admin.DetailAsync(fields.Id, 1, 10, config, f.Clock.Now, Ct))!.Task.AutomaticRecoveryAvailable);
        Assert.True((await admin.DetailAsync(audio.Id, 1, 10, config, f.Clock.Now, Ct))!.Task.AutomaticRecoveryAvailable);
        Assert.Empty((await admin.ListAsync(new(AttentionOnly: true), config, f.Clock.Now, Ct)).Items);
        Assert.Equal(0, await f.Tasks.RecoverAsync(5, ["word"], config, f.Clock.Now, Ct));
        await using var a = f.Open(); await using var b = f.Open();
        var recovered = await Task.WhenAll(new EnrichmentTaskRepository(a, f.Clock).RecoverAsync(5, [], config, f.Clock.Now, Ct),
            new EnrichmentTaskRepository(b, f.Clock).RecoverAsync(5, [], config, f.Clock.Now, Ct));
        Assert.Equal(1, recovered.Sum());
        Assert.Single(await f.Db.EnrichmentRecoveries.AsNoTracking().ToArrayAsync(Ct));
        foreach (var task in new[] { prepare, fields, audio })
        {
            var refreshed = (await f.Tasks.GetAsync(task.Id, Ct))!;
            Assert.Equal(2, refreshed.CurrentRound);
            Assert.NotNull(Round(refreshed).RecoveryId);
            Assert.True(Round(refreshed).ForceSingle);
            Assert.Equal(5, Round(refreshed).FailureBudget);
            Assert.Equal(EnrichmentState.Failed, refreshed.Rounds[0].State);
        }
        f.Clock.Now += 1001;
        fields = (await f.Executor.ExecuteAsync(fields.Id, "fields", Ct))!;
        audio = (await f.Executor.ExecuteAsync(audio.Id, "audio", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(fields).State);
        Assert.Equal(EnrichmentState.Succeeded, Round(audio).State);
        Assert.NotNull(Round(fields).RecoveryId); // field publication changes its input revision
        Assert.Equal(5, Round(fields).FailureBudget); Assert.Equal(5, Round(audio).FailureBudget);
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls);
        Assert.Equal(0, await f.Tasks.RecoverAsync(5, [], config, f.Clock.Now, Ct));
    }

    [Fact]
    public async Task AutomaticRecovery_FiveBadPlansStopAcrossRestarts_ManualRetryRemainsPossible()
    {
        await using var f = await Fixture.Create(fields: false, native: true);
        var task = await f.Ensure(Key(EnrichmentStep.Prepare));
        await FailLocally(f, task, "text.invalid_plan");
        var config = await AdminConfig(f);
        Assert.Equal(1, await f.Tasks.RecoverAsync(5, [], config, f.Clock.Now, Ct));
        for (var failure = 1; failure <= 5; failure++)
        {
            await using var db = f.Open();
            var tasks = new EnrichmentTaskRepository(db, f.Clock);
            var current = (await tasks.GetAsync(task.Id, Ct))!;
            f.Clock.Now = Round(current).NextAttemptAtUnixMs!.Value + 1;
            current = (await f.ExecutorFor(db, tasks).ExecuteAsync(task.Id, "restart", Ct))!;
            Assert.Equal(failure, Round(current).ConsumedFailures);
            Assert.Equal(failure == 5 ? EnrichmentState.Failed : EnrichmentState.Waiting, Round(current).State);
            Assert.Equal(0, await tasks.RecoverAsync(5, [], config, f.Clock.Now, Ct));
        }
        Assert.Equal(5, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls);
        await using var restarted = f.Open(); var repository = new EnrichmentTaskRepository(restarted, f.Clock);
        var detail = (await new EnrichmentAdminRepository(restarted).DetailAsync(task.Id, 1, 10, config, f.Clock.Now, Ct))!;
        Assert.False(detail.Task.AutomaticRecoveryAvailable); Assert.True(detail.Task.Current.AutomaticRecovery);
        Assert.Single((await new EnrichmentAdminRepository(restarted).ListAsync(new(AttentionOnly: true), config, f.Clock.Now, Ct)).Items);
        Assert.Equal(0, await repository.RecoverAsync(5, [], config, f.Clock.Now, Ct));
        Assert.Equal(EnrichmentRetryStatus.Started, (await repository.RetryAsync(task.Id, detail.Task.Current.Number,
            Guid.NewGuid().ToString(), config, f.Clock.Now, Ct)).Status);
        f.Http.Ipa = true;
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.ExecutorFor(restarted, repository).ExecuteAsync(task.Id, "manual", Ct))!).State);
    }

    [Theory]
    [InlineData("tts.credentials_rejected")]
    [InlineData("tts.balance_required")]
    [InlineData("text.phonetic_constraint_mismatch")]
    [InlineData("fields.equivalent_meaning")]
    public async Task AutomaticRecovery_NeverReopensCredentialsBillingOrContentConflicts(string error)
    {
        await using var f = await Fixture.Create();
        var task = await f.Ensure(Key(error.StartsWith("fields.", StringComparison.Ordinal) ? EnrichmentStep.Fields : EnrichmentStep.Prepare));
        await FailLocally(f, task, error);
        var config = await AdminConfig(f);
        Assert.Equal(0, await f.Tasks.RecoverAsync(5, [], config, f.Clock.Now, Ct));
        Assert.False((await new EnrichmentAdminRepository(f.Db).DetailAsync(task.Id, 1, 10, config, f.Clock.Now, Ct))!.Task.AutomaticRecoveryAvailable);
        Assert.Single((await f.Tasks.GetAsync(task.Id, Ct))!.Rounds);
    }

    [Fact]
    public async Task AutomaticRecovery_RequiresConfiguredDemand_AndDistinguishesLocalFromProviderRejection()
    {
        await using var f = await Fixture.Create(fields: false);
        var audio = await f.Ensure(Key());
        var guard = (await f.Tasks.ClaimAsync(audio.Id, "provider", f.Clock.Now, f.Clock.Now + 10000, Ct))!;
        var call = Guid.NewGuid().ToString();
        await f.Tasks.BeginCallAsync(guard, call, "tts", null, f.Clock.Now, Ct);
        await f.Tasks.FinishCallAsync(guard, call, EnrichmentCallResult.TerminalFailure, "tts.capability_unsupported", f.Clock.Now, null, Ct);
        var config = await AdminConfig(f);
        Assert.Equal(0, await f.Tasks.RecoverAsync(5, [], config, f.Clock.Now, Ct));
        var local = await f.Ensure(Key(locale: AudioLocale.EnUs));
        await FailLocally(f, local, "tts.capability_unsupported");
        var disabled = config with { StorageEnabled = false };
        Assert.Equal(0, await f.Tasks.RecoverAsync(5, [], disabled, f.Clock.Now, Ct));
        Assert.False((await new EnrichmentAdminRepository(f.Db).DetailAsync(local.Id, 1, 10, disabled, f.Clock.Now, Ct))!.Task.AutomaticRecoveryAvailable);
        Assert.Equal(1, await f.Tasks.RecoverAsync(5, [], config, f.Clock.Now, Ct));
        f.Clock.Now += 1001;
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Executor.ExecuteAsync(local.Id, "recovered", Ct))!).State);
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls);
        Assert.Single((await f.Tasks.GetAsync(audio.Id, Ct))!.Rounds);
    }

    [Fact]
    public async Task AutomaticRecovery_DependencyWriteFailureRollsBackReceiptAndAllRounds()
    {
        await using var f = await Fixture.Create();
        var prepare = await f.Ensure(Key(EnrichmentStep.Prepare));
        await FailLocally(f, prepare, "text.invalid_plan");
        await f.Run(Key(EnrichmentStep.Fields));
        var before = await Dump(f.Db); var config = await AdminConfig(f);
        await using var failing = f.Open(new FailRetryPreparation());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EnrichmentTaskRepository(failing, f.Clock)
            .RecoverAsync(5, [], config, f.Clock.Now, Ct));
        Assert.Equal(before, await Dump(f.Db));
        Assert.Equal(1, await f.Tasks.RecoverAsync(5, [], config, f.Clock.Now, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticRecovery_NewInputOrConfigurationHasItsOwnSingleAllowance(bool configurationChange)
    {
        await using var f = await Fixture.Create();
        var task = await f.Ensure(Key(EnrichmentStep.Prepare));
        await FailLocally(f, task, "text.invalid_plan");
        Assert.Equal(1, await f.Tasks.RecoverAsync(5, [], await AdminConfig(f), f.Clock.Now, Ct));
        f.Clock.Now += 1001;
        await FailLocally(f, (await f.Tasks.GetAsync(task.Id, Ct))!, "text.invalid_plan");
        Assert.Equal(0, await f.Tasks.RecoverAsync(5, [], await AdminConfig(f), f.Clock.Now, Ct));
        if (configurationChange) await f.Publish(new() { ["Enrichment:Text:Model"] = "replacement" });
        else await f.Db.VocabularyMeanings.ExecuteUpdateAsync(s => s.SetProperty(m => m.Meaning, "edited sense"), Ct);
        task = await f.Ensure(Key(EnrichmentStep.Prepare));
        Assert.Null(Round(task).RecoveryId);
        await FailLocally(f, task, "text.invalid_plan");
        Assert.Equal(1, await f.Tasks.RecoverAsync(5, [], await AdminConfig(f), f.Clock.Now, Ct));
        Assert.Equal(2, await f.Db.EnrichmentRecoveries.CountAsync(Ct));
    }

    [Fact]
    public async Task PhraseDecision_RemovesFalseFieldBacklog_AndInputEditsRestoreDemand()
    {
        await using var f = await Fixture.Create();
        await f.Db.VocabularyMeaningUnits.ExecuteDeleteAsync(Ct); // historical phrases have no explicit placement
        f.Http.Kind = "phrase";
        var fields = (await f.Run(Key(EnrichmentStep.Fields)))!;
        var config = await AdminConfig(f); var admin = new EnrichmentAdminRepository(f.Db);
        var detail = (await admin.DetailAsync(fields.Id, 1, 10, config, f.Clock.Now, Ct))!;
        Assert.True(detail.Task.Current.FieldsNotRequired); Assert.False(detail.Task.Missing);
        Assert.False(detail.Task.Applicable);
        Assert.Equal(1, (await admin.SummaryAsync(new(Step: "Fields"), config, f.Clock.Now, Ct)).ApplicableTargets); // other accent not classified yet
        await f.Run(Key(EnrichmentStep.Fields)); Assert.Equal(1, f.Http.TextCalls);
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Run(Key()))!).State);
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls);
        await f.Db.VocabularyMeanings.Where(m => m.Id == "meaning").ExecuteUpdateAsync(s => s.SetProperty(m => m.Meaning, "changed sense"), Ct);
        f.Http.Kind = "word";
        var refreshed = await f.Ensure(Key(EnrichmentStep.Fields));
        Assert.False(Round(refreshed).FieldsNotRequired);
        Assert.True((await admin.DetailAsync(fields.Id, 1, 10, config, f.Clock.Now, Ct))!.Task.Missing);
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Run(Key(EnrichmentStep.Fields)))!).State);
        Assert.Equal(2, f.Http.TextCalls);
    }
}
