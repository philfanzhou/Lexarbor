using System.Data.Common;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static byte[] Mp3 => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    private static EnrichmentTargetKey Key(EnrichmentStep step = EnrichmentStep.Audio, AudioLocale locale = AudioLocale.EnGb,
        string? meaning = null) => new("word", meaning, locale, step);
    private static EnrichmentRound Round(EnrichmentTask task) => task.Rounds.Single(round => round.Number == task.CurrentRound);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task FullPath_UsesRealPreparationSpeechAndUpload_WithSameRoundFieldAdvanceAndDurableEnvelope(bool fields, bool native)
    {
        await using var f = await Fixture.Create(fields, native: native);
        f.Http.Ipa = native;
        var result = (await f.Run(Key()))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State);
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls); Assert.Equal(1, f.Store.Puts);
        Assert.Equal(fields ? "liːd" : null, await f.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Equal(fields ? "v." : null, await f.Db.VocabularyMeanings.Select(row => row.PartOfSpeech).SingleAsync(Ct));
        var prepare = (await f.Tasks.EnsureAsync(Key(EnrichmentStep.Prepare),
            EnrichmentTargetExecutor.ConfigurationVersion(Key(EnrichmentStep.Prepare), f.Runtime.Current), f.Clock.Now, Ct))!;
        Assert.Single(prepare.Rounds); Assert.Equal(EnrichmentState.Succeeded, Round(prepare).State);
        Assert.Equal(Round(prepare).Input, Round(prepare).Plan!.Preparation!.InputVersion);
        Assert.Equal(fields ? "liːd" : null, Round(prepare).Plan!.Preparation!.RequiredPhonetic);
        Assert.Equal("Fixture classification", Round(prepare).Plan!.Preparation!.ClassificationBasis);
        Assert.Equal(f.Runtime.Current.TextVersion, Round(prepare).Plan!.Preparation!.ConfigurationVersion);
        await using var restart = f.Open();
        var persisted = (await new EnrichmentTaskRepository(restart, f.Clock).GetAsync(result.Id, Ct))!;
        Assert.Equal(JsonSerializer.Serialize(Round(result).Plan), JsonSerializer.Serialize(Round(persisted).Plan));
        Assert.Equal(1, Round(persisted).RequestCount); Assert.Equal(0, Round(persisted).ConsumedFailures);
        Assert.Single(await restart.AudioUploadOperations.ToListAsync(Ct));
        Assert.Equal(200, (await restart.AudioUploadOperations.SingleAsync(Ct)).ReceiptStatusCode);
        await f.Executor.ExecuteAsync(result.Id, "again", Ct);
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls);
    }

    [Fact]
    public async Task IpaEnvelope_RestartPreservesWholeSpeechControl_WithoutWritingItAsCandidate()
    {
        await using var f = await Fixture.Create(fields: false);
        f.Http.Ipa = true;
        var prepared = (await f.Run(Key(EnrichmentStep.Prepare)))!;
        var saved = Round(prepared).Plan!;
        Assert.Equal(PronunciationControl.Ipa, saved.Preparation!.Control);
        Assert.Equal("liːd", saved.Preparation.SpeechPhonetic); Assert.Null(saved.Phonetic);
        await using var restarted = f.Open();
        var tasks = new EnrichmentTaskRepository(restarted, f.Clock);
        var result = (await f.ExecutorFor(restarted, tasks).ExecuteAsync((await f.Ensure(Key())).Id, "restart", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State);
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls);
        Assert.Null(await restarted.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Null(await restarted.VocabularyMeanings.Select(row => row.PartOfSpeech).SingleAsync(Ct));
    }

    [Fact]
    public async Task LegacyContentOnlyPlan_MustBePreparedAgain_AndRelevantTextChangeRefreshesPreparation()
    {
        await using var f = await Fixture.Create(fields: false);
        var task = await f.Ensure(Key(EnrichmentStep.Prepare));
        var guard = (await f.Tasks.ClaimAsync(task.Id, "legacy", f.Clock.Now, f.Clock.Now + 1000, Ct))!;
        await f.Tasks.SavePlanAsync(guard, new("lead", "legacy", null, []), f.Clock.Now, Ct);
        await f.Tasks.CompleteAsync(guard, f.Clock.Now, Ct);
        var result = (await f.Run(Key()))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State); Assert.Equal(1, f.Http.TextCalls);
        Assert.Equal(2, (await f.Tasks.GetAsync(task.Id, Ct))!.CurrentRound);
        await f.Publish(new() { ["Enrichment:Text:Model"] = "corrected-planner" });
        var corrected = (await f.Executor.ExecuteAsync(task.Id, "changed", Ct))!;
        Assert.Equal(3, corrected.CurrentRound); Assert.Equal(2, f.Http.TextCalls);
        Assert.Equal(f.Runtime.Current.TextVersion, Round(corrected).Plan!.Preparation!.ConfigurationVersion);
        await f.Executor.ExecuteAsync(result.Id, "already-published", Ct);
        Assert.Equal(1, f.Http.SpeechCalls);
    }

    [Theory]
    [InlineData(false, true, EnrichmentWaitReason.Disabled)]
    [InlineData(true, false, EnrichmentWaitReason.StorageDisabled)]
    public async Task PersistedSpecificPreparation_WaitsForAudioAndStorage_EvenWhenFieldFillingIsEnabled(
        bool audioEnabled, bool storageEnabled, EnrichmentWaitReason expected)
    {
        await using var f = await Fixture.Create();
        await f.Db.VocabularyMeanings.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "liːd"), Ct);
        await f.Publish(new() { ["Enrichment:AudioEnabled"] = audioEnabled.ToString() }); f.Operations.Enabled = storageEnabled;
        var key = Key(EnrichmentStep.Prepare, meaning: "meaning");
        var version = EnrichmentTargetExecutor.ConfigurationVersion(key, f.Runtime.Current);
        var result = (await f.Run(key))!;
        Assert.Equal(EnrichmentState.Waiting, Round(result).State); Assert.Equal(expected, Round(result).WaitReason);
        Assert.Equal(0, Round(result).RequestCount); Assert.Equal(0, Round(result).ConsumedFailures);
        Assert.Equal(0, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls);
        await f.Publish(new() { ["Enrichment:FieldsEnabled"] = "false" });
        Assert.Equal(version, EnrichmentTargetExecutor.ConfigurationVersion(key, f.Runtime.Current));
    }

    [Fact]
    public async Task DefaultFieldPreparation_CanFillFieldsWhenAudioAndStorageAreDisabled()
    {
        await using var f = await Fixture.Create();
        await f.Publish(new() { ["Enrichment:AudioEnabled"] = "false" }); f.Operations.Enabled = false;
        var result = (await f.Run(Key(EnrichmentStep.Fields)))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State);
        Assert.Equal("liːd", await f.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Equal("v.", await f.Db.VocabularyMeanings.Select(row => row.PartOfSpeech).SingleAsync(Ct));
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls); Assert.Equal(0, f.Store.Puts);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OmittedFieldCandidates_RemainFailedRatherThanCompleted_WithoutBlockingIndependentAudio(bool ipa, bool pos)
    {
        await using var f = await Fixture.Create(); f.Http.OmitPhonetic = ipa; f.Http.OmitPartsOfSpeech = pos;
        var fields = (await f.Run(Key(EnrichmentStep.Fields)))!;
        Assert.Equal(EnrichmentState.Failed, Round(fields).State); Assert.Equal("fields.candidates_missing", Round(fields).ErrorCode);
        Assert.Equal(0, Round(fields).ConsumedFailures); Assert.Equal(0, Round(fields).RequestCount);
        Assert.Equal(ipa ? null : "liːd", await f.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Equal(pos ? null : "v.", await f.Db.VocabularyMeanings.Select(row => row.PartOfSpeech).SingleAsync(Ct));
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Run(Key()))!).State);
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls);
    }

    [Fact]
    public async Task FieldCommit_RollsBackAllMutationsAndPlanAdvance_AndQueuedWriterRechecksExpiry()
    {
        await using var f = await Fixture.Create();
        var task = await f.Ensure(Key(EnrichmentStep.Prepare));
        var guard = (await f.Tasks.ClaimAsync(task.Id, "fields", f.Clock.Now, f.Clock.Now + 1000, Ct))!;
        var snapshot = (await new EnrichmentExecutionRepository(f.Db, f.Tasks, f.Clock).ReadAsync(task.Target, Ct))!;
        var input = AudioSelectionService.GetInput(snapshot.Request.Word, null, AudioLocale.EnGb);
        var plan = EnrichmentPreparedPlans.Store(new(new("lead", "read correctly", "liːd", [new("meaning", "v.")]),
            input, guard.Input, f.Runtime.Current.TextVersion!, "word", "Fixture classification", null), null);
        await f.Tasks.SavePlanAsync(guard, plan, f.Clock.Now, Ct);
        var fault = new FailFieldPlanWrite();
        await using var writer = f.Open(fault); var tasks = new EnrichmentTaskRepository(writer, f.Clock);
        var fields = new EnrichmentExecutionRepository(writer, tasks, f.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fields.CommitFieldsAsync(guard, plan, f.Clock.Now, Ct));
        Assert.Null(await f.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Null(await f.Db.VocabularyMeanings.Select(row => row.PartOfSpeech).SingleAsync(Ct));
        Assert.Equal(guard.Input, Round((await f.Tasks.GetAsync(task.Id, Ct))!).Input);
        Assert.Equal(JsonSerializer.Serialize(plan), JsonSerializer.Serialize(Round((await f.Tasks.GetAsync(task.Id, Ct))!).Plan));
        fault.Enabled = false;
        await using var holder = f.Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new UnitOfWork(holder).ExecuteInTransactionAsync(async () => { entered.SetResult(); await release.Task; return 0; }, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var queued = fields.CommitFieldsAsync(guard, plan, f.Clock.Now, Ct);
        f.Clock.Now += 1000; release.SetResult(); await holding;
        Assert.Null(await queued);
        Assert.Null(await f.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        var fresh = (await tasks.ClaimAsync(task.Id, "fresh", f.Clock.Now, f.Clock.Now + 1000, Ct))!;
        var committed = (await fields.CommitFieldsAsync(fresh, plan, f.Clock.Now, Ct))!;
        Assert.Equal(guard.Round, committed.Guard.Round); Assert.Equal("liːd", committed.Plan.Preparation!.RequiredPhonetic);
        Assert.True(await tasks.CompleteAsync(committed.Guard, f.Clock.Now, Ct));
    }

    [Fact]
    public async Task BackgroundUsesActualOperationFactory_AndRetainsSameStorageGenerationDuringRotation()
    {
        await using var f = await Fixture.Create(fields: false);
        var stores = new StoreFactory(f.Store);
        using var runtime = new AudioRuntimeConfiguration(Options.Create(new AudioOptions
        {
            Enabled = true,
            InternalEndpoint = "http://127.0.0.1:9/",
            PublicEndpoint = "http://127.0.0.1:9/",
            AccessKey = "synthetic",
            SecretKey = "synthetic",
            Bucket = "audio"
        }), stores);
        f.Store.BeforePut = async () =>
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audio:Enabled"] = "true",
                ["Audio:InternalEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:PublicEndpoint"] = "http://127.0.0.1:9/",
                ["Audio:AccessKey"] = "synthetic-rotated",
                ["Audio:SecretKey"] = "synthetic-rotated",
                ["Audio:Bucket"] = "audio"
            }).Build();
            using var lifetime = (IDisposable)config;
            using var change = await runtime.PrepareAsync(config, Ct); Assert.NotNull(change); change.Publish();
            Assert.Equal(0, f.Store.Disposals);
        };
        var executor = f.ExecutorFor(f.Db, f.Tasks, new AudioUploadOperationFactory(runtime, f.Uploads, f.Clock));
        Assert.Equal(EnrichmentState.Succeeded, Round((await executor.ExecuteAsync((await f.Ensure(Key())).Id, "factory", Ct))!).State);
        Assert.Equal(1, f.Store.Puts); Assert.Equal(1, f.Store.Disposals);
        Assert.Equal(2, stores.Stores.Count); Assert.Equal(0, stores.Stores[1].Puts);
    }

    [Fact]
    public async Task FieldAdvanceThatExceedsWholePlanBound_RollsBackAndStopsWithoutInfiniteLeaseRetries()
    {
        await using var f = await Fixture.Create();
        var key = Key(EnrichmentStep.Prepare); var task = await f.Ensure(key);
        var guard = (await f.Tasks.ClaimAsync(task.Id, "bounded", f.Clock.Now, f.Clock.Now + 1000, Ct))!;
        var snapshot = (await new EnrichmentExecutionRepository(f.Db, f.Tasks, f.Clock).ReadAsync(key, Ct))!;
        var input = AudioSelectionService.GetInput(snapshot.Request.Word, null, key.Locale);
        var plan = EnrichmentPreparedPlans.Store(new(new(new('x', 4096), new('中', 2048), new('p', 512), []),
            input, guard.Input, f.Runtime.Current.TextVersion!, "word", "Fixture classification", null), null);
        while (JsonSerializer.Serialize(plan).Length > 16384) plan = plan with { PronunciationHint = plan.PronunciationHint![..^1] };
        Assert.True(await f.Tasks.SavePlanAsync(guard, plan, f.Clock.Now, Ct));
        f.Clock.Now += 1000;
        var result = (await f.Executor.ExecuteAsync(task.Id, "reclaimed", Ct))!;
        Assert.Equal(EnrichmentState.Failed, Round(result).State); Assert.Equal("fields.plan_too_large", Round(result).ErrorCode);
        Assert.Null(await f.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Equal(guard.Input, Round(result).Input); Assert.Equal(0, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls);
    }

    [Theory]
    [InlineData("uncertain")]
    [InlineData("invalid")]
    [InlineData("capability")]
    [InlineData("missing")]
    [InlineData("disabled")]
    public async Task PreparationAndCapabilityGates_NeverDispatchSpeech(string scenario)
    {
        await using var f = await Fixture.Create(fields: false);
        if (scenario is "uncertain" or "invalid") f.Http.PlanOutcome = scenario;
        if (scenario == "capability") await f.Publish(new() { ["Enrichment:Tts:Model"] = "unknown" });
        if (scenario == "missing") await f.Publish(new() { ["Enrichment:Tts:Uk:Voice"] = "" });
        if (scenario == "disabled") await f.Publish(new() { ["Enrichment:AudioEnabled"] = "false" });
        var result = (await f.Run(Key()))!;
        Assert.Equal(0, f.Http.SpeechCalls); Assert.Equal(0, f.Store.Puts);
        Assert.Equal(0, Round(result).RequestCount);
        Assert.Equal(scenario is "missing" or "disabled" or "uncertain" ? EnrichmentState.Waiting : EnrichmentState.Failed, Round(result).State);
    }

    [Fact]
    public async Task SeparateAccents_OnlyFailedSideContinues_ThrottleDoesNotSpendFailureBudget()
    {
        await using var f = await Fixture.Create(fields: false);
        var uk = (await f.Run(Key()))!;
        f.Http.SpeechOutcome = "throttle";
        var us = (await f.Run(Key(locale: AudioLocale.EnUs)))!;
        Assert.Equal(EnrichmentState.Waiting, Round(us).State); Assert.Equal(0, Round(us).ConsumedFailures);
        Assert.Equal(1, Round(us).RequestCount); Assert.Equal(2, f.Http.TextCalls);
        f.Clock.Now = Round(us).NextAttemptAtUnixMs!.Value;
        f.Http.SpeechOutcome = "ready";
        us = (await f.Executor.ExecuteAsync(us.Id, "retry", Ct))!;
        await f.Executor.ExecuteAsync(uk.Id, "already", Ct);
        Assert.Equal(EnrichmentState.Succeeded, Round(us).State); Assert.Equal(2, Round(us).RequestCount);
        Assert.Equal(0, Round(us).ConsumedFailures); Assert.Equal(2, f.Http.TextCalls); Assert.Equal(3, f.Http.SpeechCalls);
        Assert.Equal(2, await f.Db.VocabularyAudioBindings.CountAsync(Ct));
    }

    [Theory]
    [InlineData("service", 5)]
    [InlineData("balance", 1)]
    public async Task ActualFailuresAndBillingBlocks_StopWithoutHotRetry_AndRestartDoesNotReset(string outcome, int calls)
    {
        await using var f = await Fixture.Create(fields: false); f.Http.SpeechOutcome = outcome;
        var result = (await f.Run(Key()))!;
        for (var i = 1; i < calls; i++)
        {
            f.Clock.Now = Round(result).NextAttemptAtUnixMs!.Value;
            result = (await f.Executor.ExecuteAsync(result.Id, "retry", Ct))!;
        }
        Assert.Equal(EnrichmentState.Failed, Round(result).State); Assert.Equal(calls, Round(result).ConsumedFailures);
        Assert.Equal(calls, Round(result).RequestCount);
        f.Clock.Now += 1_000_000;
        await f.Executor.ExecuteAsync(result.Id, "recovered-service", Ct);
        Assert.Equal(calls, f.Http.SpeechCalls); Assert.Equal(1, f.Http.TextCalls);
        await using var restart = f.Open();
        var tasks = new EnrichmentTaskRepository(restart, f.Clock);
        await f.ExecutorFor(restart, tasks).ExecuteAsync(result.Id, "restart", Ct);
        Assert.Equal(calls, f.Http.SpeechCalls);
        await f.Publish(new() { ["Enrichment:Tts:Us:Voice"] = "new-us" });
        await f.Executor.ExecuteAsync(result.Id, "unrelated", Ct); Assert.Equal(calls, f.Http.SpeechCalls);
        await f.Publish(new() { ["Enrichment:Tts:Uk:Voice"] = "new-uk" }); f.Http.SpeechOutcome = "ready";
        result = (await f.Executor.ExecuteAsync(result.Id, "fixed", Ct))!;
        Assert.Equal(2, result.CurrentRound); Assert.Equal(EnrichmentState.Succeeded, Round(result).State);
        Assert.Equal(calls, result.Rounds[0].ConsumedFailures); Assert.Equal(0, Round(result).ConsumedFailures);
    }

    [Theory]
    [InlineData("field")]
    [InlineData("position")]
    [InlineData("binding")]
    [InlineData("fence")]
    public async Task FinalFieldTransaction_RejectsRacingEditUploadOrSameInputOldFence(string race)
    {
        await using var f = await Fixture.Create();
        f.Http.BeforeTextResponse = async () =>
        {
            await using var other = f.Open();
            await new UnitOfWork(other).ExecuteInTransactionAsync(async () =>
            {
                if (race == "field") await other.Vocabularies.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "human"), Ct);
                if (race == "position") await other.VocabularyMeaningUnits.ExecuteUpdateAsync(set => set.SetProperty(row => row.EntryKind, "phrase"), Ct);
                if (race == "fence") await other.EnrichmentRounds.Where(row => row.State == EnrichmentState.Running)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseFence, row => row.LeaseFence + 1), Ct);
                if (race == "binding")
                {
                    var input = (await new EnrichmentTaskRepository(other, f.Clock).ReadInputAsync(Key(), Ct))!;
                    other.AudioAssets.Add(new() { Id = "human", Bucket = "audio", ObjectKey = "audio/human.mp3", Sha256 = new('a', 64), ByteLength = 500, CreatedAtUnixMs = 1, FirstPublishedAtUnixMs = 2 });
                    other.VocabularyAudioBindings.Add(new() { Id = "human", WordId = "word", Locale = AudioLocale.EnGb, AssetId = "human", SourceRevision = input.SourceRevision });
                    await other.SaveChangesAsync(Ct);
                }
                return 0;
            }, Ct);
        };
        await f.Run(Key(EnrichmentStep.Prepare));
        Assert.Equal(race == "field" ? "human" : null, await f.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Null(await f.Db.VocabularyMeanings.Select(row => row.PartOfSpeech).SingleAsync(Ct));
        Assert.Equal(0, f.Http.SpeechCalls);
    }

    [Fact]
    public async Task PhraseAndMixedPositions_RespectSharedFields_AndEquivalentPosConflictDoesNotMergeIds()
    {
        await using var f = await Fixture.Create();
        await f.Db.VocabularyMeaningUnits.ExecuteUpdateAsync(set => set.SetProperty(row => row.EntryKind, "phrase"), Ct);
        f.Http.Kind = "phrase";
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Run(Key(EnrichmentStep.Fields)))!).State);
        await f.Run(Key());
        Assert.Null(await f.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Null(await f.Db.VocabularyMeanings.Select(row => row.PartOfSpeech).SingleAsync(Ct));
        Assert.Equal(1, f.Http.SpeechCalls);

        await using var mixed = await Fixture.Create();
        mixed.Db.VocabularyMeanings.Add(new() { Id = "duplicate", VocabularyId = "word", BookId = "book", PartOfSpeech = "v.", Meaning = "guide" });
        mixed.Db.VocabularyMeaningUnits.Add(new() { MeaningId = "meaning", UnitId = "unit", BookId = "book", EntryKind = "phrase" });
        await mixed.Db.SaveChangesAsync(Ct); mixed.Db.ChangeTracker.Clear();
        await mixed.Run(Key(EnrichmentStep.Fields));
        Assert.Equal("liːd", await mixed.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Null(await mixed.Db.VocabularyMeanings.Where(row => row.Id == "meaning").Select(row => row.PartOfSpeech).SingleAsync(Ct));
        await mixed.Db.VocabularyMeaningUnits.Where(row => row.EntryKind == "phrase").ExecuteDeleteAsync(Ct);
        var result = (await mixed.Run(Key(EnrichmentStep.Fields)))!;
        Assert.Equal(EnrichmentState.Failed, Round(result).State); Assert.Equal("fields.equivalent_meaning", Round(result).ErrorCode);
        Assert.Equal(2, await mixed.Db.VocabularyMeanings.CountAsync(Ct));
        Assert.Null(await mixed.Db.VocabularyMeanings.Where(row => row.Id == "meaning").Select(row => row.PartOfSpeech).SingleAsync(Ct));
        Assert.Equal(EnrichmentState.Succeeded, Round((await mixed.Run(Key()))!).State);
    }

    [Theory]
    [InlineData("change")]
    [InlineData("clear")]
    [InlineData("aba")]
    [InlineData("fence")]
    [InlineData("task-lease")]
    [InlineData("upload-lease")]
    [InlineData("manual")]
    [InlineData("delete")]
    public async Task PublicationWriter_RejectsStaleExecutionAndConcurrentBinding_LeavingNeverPublishedCleanup(string race)
    {
        await using var f = await Fixture.Create(fields: false);
        await f.Db.VocabularyMeanings.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "liːd"), Ct);
        f.Store.BeforePut = async () =>
        {
            await using var other = f.Open();
            if (race is "change" or "clear" or "aba")
            {
                var tasks = new EnrichmentTaskRepository(other, f.Clock);
                var repo = new MeaningPhoneticRepository(other, new AudioInvalidationRepository(other, f.Clock), tasks);
                foreach (var value in race == "aba" ? new[] { "led", "liːd" } : new[] { race == "clear" ? null : "led" })
                {
                    var before = await repo.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Ct);
                    await new MeaningPhoneticEditService(repo, new UnitOfWork(other)).ApplyAsync(
                        [new("book", "word", "meaning", AudioLocale.EnGb, true, value, before)],
                        new(f.Runtime.Current.TextVersion, f.Runtime.Current.UkTtsVersion, f.Runtime.Current.UsTtsVersion), f.Clock.Now, Ct);
                }
            }
            else if (race == "manual")
            {
                var ownInput = (await new EnrichmentTaskRepository(other, f.Clock).ReadInputAsync(Key(meaning: "meaning"), Ct))!;
                var upload = new AudioUploadService(new AudioUploadRepository(other, f.Clock), new Store(), new(true, "audio", "audio/", 60), f.Clock);
                Assert.Equal(200, (await upload.UploadAsync(new(Guid.NewGuid().ToString(), "word", "meaning", AudioLocale.EnGb,
                    ownInput.SourceRevision, null, Mp3, "manual", null), Ct)).StatusCode);
            }
            else await new UnitOfWork(other).ExecuteInTransactionAsync(async () =>
            {
                if (race == "delete") await other.VocabularyMeanings.ExecuteDeleteAsync(Ct);
                if (race == "fence") await other.EnrichmentRounds.Where(row => row.State == EnrichmentState.Running)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseFence, row => row.LeaseFence + 1), Ct);
                if (race == "task-lease") await other.EnrichmentRounds.Where(row => row.State == EnrichmentState.Running)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.LeaseExpiresAtUnixMs, f.Clock.Now), Ct);
                if (race == "upload-lease") await other.AudioUploadOperations.ExecuteUpdateAsync(set => set.SetProperty(row => row.UploadLeaseExpiresAtUnixMs, f.Clock.Now), Ct);
                return 0;
            }, Ct);
        };
        var result = (await f.Run(Key(meaning: "meaning")))!;
        Assert.Equal(1, f.Http.SpeechCalls); Assert.Equal(1, f.Store.Puts);
        var automatic = await f.Db.AudioUploadOperations.SingleAsync(row => row.Producer == "enrichment", Ct);
        Assert.NotEqual(200, automatic.ReceiptStatusCode);
        Assert.Null((await f.Db.AudioAssets.SingleAsync(row => row.Id == automatic.AssetId, Ct)).FirstPublishedAtUnixMs);
        Assert.Equal(race == "manual" ? 1 : 0, await f.Db.VocabularyAudioBindings.CountAsync(Ct));
        f.Clock.Now += 86_400_001;
        Assert.NotNull(await f.Uploads.ClaimCleanupAsync(automatic.IdempotencyKey, "cleanup", f.Clock.Now, f.Clock.Now + 1000, Ct));
        Assert.NotEqual(EnrichmentState.Succeeded, Round(result).State);
    }

    [Theory]
    [InlineData("change")]
    [InlineData("clear")]
    [InlineData("aba")]
    public async Task SpecificPhoneticEditsDuringTts_NeverPublishOldAudio(string change)
    {
        await using var f = await Fixture.Create(fields: false);
        await f.Db.VocabularyMeanings.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "liːd"), Ct);
        f.Http.BeforeSpeechResponse = async _ =>
        {
            await using var other = f.Open();
            var tasks = new EnrichmentTaskRepository(other, f.Clock);
            var phonetics = new MeaningPhoneticRepository(other, new AudioInvalidationRepository(other, f.Clock), tasks);
            foreach (var value in change == "aba" ? new[] { "led", "liːd" } : new[] { change == "clear" ? null : "led" })
            {
                var before = await phonetics.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Ct);
                await new MeaningPhoneticEditService(phonetics, new UnitOfWork(other)).ApplyAsync(
                    [new("book", "word", "meaning", AudioLocale.EnGb, true, value, before)],
                    new(f.Runtime.Current.TextVersion, f.Runtime.Current.UkTtsVersion, f.Runtime.Current.UsTtsVersion), f.Clock.Now, Ct);
            }
        };
        var result = (await f.Run(Key(meaning: "meaning")))!;
        Assert.NotEqual(EnrichmentState.Succeeded, Round(result).State);
        Assert.Empty(await f.Db.VocabularyAudioBindings.ToListAsync(Ct));
        Assert.Equal(1, f.Http.SpeechCalls);
        Assert.All(await f.Db.AudioUploadOperations.ToListAsync(Ct), op => Assert.NotEqual(200, op.ReceiptStatusCode));
        Assert.All(await f.Db.AudioAssets.ToListAsync(Ct), asset => Assert.Null(asset.FirstPublishedAtUnixMs));
    }

    [Fact]
    public async Task ExistingHumanDefaultFieldsAndStaleOwnAudio_ArePreserved()
    {
        await using var f = await Fixture.Create();
        await f.Db.Vocabularies.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "liːd"), Ct);
        await f.Db.VocabularyMeanings.ExecuteUpdateAsync(set => set.SetProperty(row => row.PartOfSpeech, "n."), Ct);
        var first = (await f.Run(Key()))!;
        Assert.Equal("liːd", await f.Db.Vocabularies.Select(row => row.PhoneticUk).SingleAsync(Ct));
        Assert.Equal("n.", await f.Db.VocabularyMeanings.Select(row => row.PartOfSpeech).SingleAsync(Ct));
        var asset = await f.Db.VocabularyAudioBindings.Select(row => row.AssetId).SingleAsync(Ct);
        await f.Db.Vocabularies.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "led"), Ct);
        var skipped = (await f.Executor.ExecuteAsync(first.Id, "stale", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(skipped).State);
        Assert.Equal(asset, await f.Db.VocabularyAudioBindings.Select(row => row.AssetId).SingleAsync(Ct));
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls);
    }

    [Fact]
    public async Task PublishedButTaskCompletionLost_RecoversPermanentReceiptBeforeAnyNewPlanOrTts()
    {
        await using var f = await Fixture.Create(fields: false);
        var task = await f.Ensure(Key());
        var guard = (await f.Tasks.ClaimAsync(task.Id, "crashed", f.Clock.Now, f.Clock.Now + 1000, Ct))!;
        var uploadKey = Guid.NewGuid().ToString();
        Assert.True(await f.Tasks.BeginCallAsync(guard, Guid.NewGuid().ToString(), "tts", uploadKey, f.Clock.Now, Ct));
        var result = await new AudioUploadService(f.Uploads, f.Store, new(true, "audio", "audio/", 60), f.Clock)
            .UploadAsync(new(uploadKey, "word", null, AudioLocale.EnGb, guard.Input.SourceRevision, null, Mp3, "enrichment", null), Ct, guard);
        Assert.Equal(200, result.StatusCode);
        f.Clock.Now += 2000;
        await using var restart = f.Open(); var tasks = new EnrichmentTaskRepository(restart, f.Clock);
        var recovered = (await f.ExecutorFor(restart, tasks).ExecuteAsync(task.Id, "restart", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(recovered).State); Assert.Single(recovered.Rounds);
        Assert.Equal(uploadKey, Assert.Single(recovered.Calls).UploadIdempotencyKey);
        Assert.Equal(EnrichmentCallResult.Succeeded, recovered.Calls[0].Result);
        Assert.Equal(0, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls); Assert.Equal(1, f.Store.Puts);
    }

    [Fact]
    public async Task LostPutResponseAndUnknownStartedCall_ReconcileOldKeyBeforeFreshAttempt()
    {
        await using var f = await Fixture.Create(fields: false); f.Store.LosePutResponse = true;
        var failed = (await f.Run(Key()))!;
        var oldKey = Assert.Single(failed.Calls).UploadIdempotencyKey;
        Assert.Equal(EnrichmentState.Waiting, Round(failed).State); Assert.Equal(1, Round(failed).ConsumedFailures);
        Assert.Equal(503, (await f.Uploads.GetAsync(oldKey!, Ct))!.Operation.Receipt!.StatusCode);
        Assert.Single(f.Store.Objects); Assert.Empty(await f.Db.VocabularyAudioBindings.ToListAsync(Ct));
        f.Clock.Now = Round(failed).NextAttemptAtUnixMs!.Value; f.Store.LosePutResponse = false;
        var done = (await f.Executor.ExecuteAsync(failed.Id, "retry", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(done).State);
        Assert.Equal(2, done.Calls.Select(call => call.UploadIdempotencyKey).Distinct().Count());
        Assert.Equal(2, f.Http.SpeechCalls); Assert.Equal(1, f.Http.TextCalls);
    }

    [Fact]
    public async Task UnknownActiveUpload_DoesNotGenerateNewKey_AndExpiredReceiptConsumesOnlyOnce()
    {
        await using var f = await Fixture.Create(fields: false);
        await f.Run(Key(EnrichmentStep.Prepare));
        var task = await f.Ensure(Key());
        var guard = (await f.Tasks.ClaimAsync(task.Id, "crashed", f.Clock.Now, f.Clock.Now + 1000, Ct))!;
        var key = Guid.NewGuid().ToString();
        await f.Tasks.BeginCallAsync(guard, Guid.NewGuid().ToString(), "tts", key, f.Clock.Now, Ct);
        var reserved = await f.Uploads.ReserveAsync(new(key, new('a', 64), "word", null, AudioLocale.EnGb,
            guard.Input.SourceRevision, null, new('b', 64), Mp3.Length, "audio"), "upload", f.Clock.Now, f.Clock.Now + 5000, Ct);
        Assert.Equal(AudioReservationDisposition.Created, reserved.Disposition);
        f.Clock.Now += 1001;
        await f.Executor.ExecuteAsync(task.Id, "restart", Ct);
        Assert.Equal(0, f.Http.SpeechCalls); Assert.Single((await f.Tasks.GetAsync(task.Id, Ct))!.Calls);
        f.Clock.Now += 900_001;
        var waiting = (await f.Executor.ExecuteAsync(task.Id, "expire", Ct))!;
        Assert.Equal(EnrichmentState.Waiting, Round(waiting).State); Assert.Equal(1, Round(waiting).ConsumedFailures);
        Assert.Single(waiting.Calls); Assert.Equal(key, waiting.Calls[0].UploadIdempotencyKey);
        Assert.Equal("audio.upload_expired", (await f.Uploads.GetAsync(key, Ct))!.Operation.Receipt!.ErrorCode);
    }

    [Fact]
    public async Task InheritedLegacyOverrideIsUntouched_ExplicitSameReadingUsesOwnMissingDespiteFallback()
    {
        await using var f = await Fixture.Create(fields: false);
        var defaultResult = (await f.Run(Key()))!;
        var binding = await f.Db.VocabularyAudioBindings.SingleAsync(Ct);
        f.Db.VocabularyAudioBindings.Add(new()
        {
            Id = "legacy",
            WordId = "word",
            MeaningId = "meaning",
            Locale = AudioLocale.EnGb,
            AssetId = binding.AssetId,
            SourceRevision = binding.SourceRevision
        });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        Assert.Null(await f.Tasks.EnsureAsync(Key(meaning: "meaning"), "anything", f.Clock.Now, Ct));
        Assert.Equal(2, await f.Db.VocabularyAudioBindings.CountAsync(Ct));
        await f.Db.VocabularyAudioBindings.Where(row => row.Id == "legacy").ExecuteDeleteAsync(Ct);
        await f.Db.Vocabularies.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "liːd"), Ct);
        await f.Db.VocabularyMeanings.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "liːd"), Ct);
        var before = await new AudioReadRepository(f.Db).GetSnapshotAsync("word", "meaning", Ct);
        Assert.Null(AudioSelectionService.Select(before, AudioLocale.EnGb).OwnBinding);
        Assert.NotNull(AudioSelectionService.Select(before, AudioLocale.EnGb).EffectiveBinding);
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Run(Key(meaning: "meaning")))!).State);
        Assert.Equal(2, f.Http.SpeechCalls);
        Assert.NotEqual(binding.AssetId, (await f.Db.VocabularyAudioBindings.SingleAsync(row => row.MeaningId == "meaning", Ct)).AssetId);
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Tasks.GetAsync(defaultResult.Id, Ct))!).State);
    }

    [Fact]
    public async Task SharedAdmission_FullQueueAndCallerCancellationAreRecoverable_StorageDisabledDoesNotCallTts()
    {
        await using var f = await Fixture.Create(fields: false, capacity: 1);
        using (await f.Admission.EnterAsync(Ct))
        {
            var full = (await f.Run(Key()))!;
            Assert.Equal(EnrichmentWaitReason.Throttled, Round(full).WaitReason);
            Assert.Equal(0, Round(full).RequestCount); Assert.Equal(0, f.Http.SpeechCalls);
            f.Clock.Now = Round(full).NextAttemptAtUnixMs!.Value;
        }
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Run(Key()))!).State);
        await using var cancelled = await Fixture.Create(fields: false);
        using var cancellation = new CancellationTokenSource();
        cancelled.Http.BeforeSpeechResponse = async token => { cancellation.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var task = await cancelled.Ensure(Key());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Executor.ExecuteAsync(task.Id, "cancel", cancellation.Token));
        Assert.Equal(0, cancelled.Store.Puts); Assert.Equal(0, Round((await cancelled.Tasks.GetAsync(task.Id, Ct))!).ConsumedFailures);
        cancelled.Clock.Now += 900_001; cancelled.Http.BeforeSpeechResponse = null;
        var unknown = (await cancelled.Executor.ExecuteAsync(task.Id, "recover", Ct))!;
        Assert.Equal(EnrichmentState.Waiting, Round(unknown).State);
        cancelled.Clock.Now = Round(unknown).NextAttemptAtUnixMs!.Value;
        Assert.Equal(EnrichmentState.Succeeded, Round((await cancelled.Executor.ExecuteAsync(task.Id, "retry", Ct))!).State);
        await using var disabled = await Fixture.Create(fields: false); disabled.Operations.Enabled = false;
        var waiting = (await disabled.Run(Key()))!;
        Assert.Equal(EnrichmentWaitReason.StorageDisabled, Round(waiting).WaitReason); Assert.Equal(0, disabled.Http.SpeechCalls);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("small")]
    public async Task BackgroundCannotBypassSharedMp3ValidationOrAutomaticMinimum(string bytes)
    {
        await using var f = await Fixture.Create(fields: false);
        f.Http.AudioBytes = bytes == "invalid" ? new byte[500] : SmallMp3();
        Assert.True(bytes != "small" || AudioUploadContentValidation.IsValid(f.Http.AudioBytes, 1000));
        Assert.False(AudioUploadContentValidation.IsValid(f.Http.AudioBytes, 1000, generated: true));
        var failed = (await f.Run(Key()))!;
        Assert.Equal(EnrichmentState.Waiting, Round(failed).State); Assert.Equal(1, Round(failed).ConsumedFailures);
        Assert.Equal(0, f.Store.Puts);
    }

    private static byte[] SmallMp3()
    {
        var bytes = new byte[52];
        foreach (var i in new[] { 0, 26 }) { bytes[i] = 255; bytes[i + 1] = 243; bytes[i + 2] = 16; }
        return bytes;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = VocabularyWebApplicationFactory.CreateGateSafeDirectory("executor-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _settings;
        private readonly List<IDisposable> _configurations = [];
        public VocabularyDbContext Db { get; }
        public EnrichmentTaskRepository Tasks { get; }
        public AudioUploadRepository Uploads { get; }
        public Clock Clock { get; } = new();
        public Handler Http { get; } = new();
        private readonly HttpFactory _clients;
        public Store Store { get; } = new();
        public Operations Operations { get; }
        public AudioUploadAdmission Admission { get; }
        public EnrichmentRuntimeConfiguration Runtime { get; }
        public EnrichmentTargetExecutor Executor { get; }
        public bool Shared { get; }
        private Fixture(bool fields, int capacity, bool native, bool shared)
        {
            Shared = shared;
            _settings = new()
            {
                ["Enrichment:FieldsEnabled"] = fields.ToString(),
                ["Enrichment:AudioEnabled"] = "true",
                ["Enrichment:Text:Endpoint"] = "https://planner.example.test/v1",
                ["Enrichment:Text:Model"] = "planner",
                ["Enrichment:Text:ApiKey"] = "synthetic-planner",
                ["Enrichment:Tts:Provider"] = native ? "MiniMaxNative" : "OpenAICompatible",
                ["Enrichment:Tts:Endpoint"] = native ? "https://api.minimax.io/v1" : "https://api.openai.com/v1",
                ["Enrichment:Tts:Model"] = native ? "speech-2.8-hd" : "gpt-4o-mini-tts",
                ["Enrichment:Tts:ApiKey"] = "synthetic-speech",
                ["Enrichment:Tts:Uk:Voice"] = "uk",
                ["Enrichment:Tts:Us:Voice"] = "us",
                ["Enrichment:Tts:Uk:Speed"] = "1",
                ["Enrichment:Tts:Us:Speed"] = "1"
            };
            Db = Open(); Tasks = new(Db, Clock); Uploads = new(Db, Clock);
            Runtime = new(Build(), new(_root)); Publish([]).GetAwaiter().GetResult();
            _clients = new(Http); Operations = new(Uploads, Store, Clock);
            Admission = new(capacity, 0, TimeSpan.FromMilliseconds(10)); Store.AssertOutsideTransaction = () => Assert.Null(Db.Database.CurrentTransaction);
            Http.AssertOutsideTransaction = Store.AssertOutsideTransaction;
            Executor = ExecutorFor(Db, Tasks);
        }
        public static async Task<Fixture> Create(bool fields = true, int capacity = 2, bool native = false, bool shared = false)
        {
            var f = new Fixture(fields, capacity, native, shared);
            await f.Db.Database.MigrateAsync(Ct);
            f.Db.VocabularyBooks.Add(new() { Id = "book", BookName = "Fixture", Status = true });
            f.Db.Vocabularies.Add(new() { Id = "word", Word = "lead" });
            await f.Db.SaveChangesAsync(Ct);
            f.Db.VocabularyMeanings.Add(new() { Id = "meaning", BookId = "book", VocabularyId = "word", Meaning = "guide" });
            f.Db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "book", Number = 1 });
            await f.Db.SaveChangesAsync(Ct);
            f.Db.VocabularyMeaningUnits.Add(new() { UnitId = "unit", BookId = "book", MeaningId = "meaning", EntryKind = "word" });
            await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear(); return f;
        }
        public VocabularyDbContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "fixture.db")};Pooling=False").AddInterceptors(interceptors).Options);
        public EnrichmentTargetExecutor ExecutorFor(VocabularyDbContext db, EnrichmentTaskRepository tasks,
            IAudioUploadOperationFactory? operations = null) => new(tasks,
            new EnrichmentExecutionRepository(db, tasks, Clock), new AudioUploadRepository(db, Clock),
            new OpenAiCompatiblePronunciationPreparationClient(_clients, Runtime, Clock), new PronunciationSpeechClient(_clients, Runtime, Clock),
            operations ?? new Operations(new AudioUploadRepository(db, Clock), Store, Clock) { SettingsSource = Operations }, Admission, Runtime, Clock,
            Shared ? new EnrichmentServiceGate(new EnrichmentSchedulerRepository(db, tasks), Runtime, Clock) : null);
        public Task<EnrichmentTask> Ensure(EnrichmentTargetKey key) => EnsureCore(key);
        private async Task<EnrichmentTask> EnsureCore(EnrichmentTargetKey key) => (await Tasks.EnsureAsync(key,
            EnrichmentTargetExecutor.ConfigurationVersion(key, Runtime.Current), Clock.Now, Ct))!;
        public async Task<EnrichmentTask?> Run(EnrichmentTargetKey key) => await Executor.ExecuteAsync((await Ensure(key)).Id, "worker", Ct);
        private IConfigurationRoot Build()
        { var config = new ConfigurationBuilder().AddInMemoryCollection(_settings).Build(); _configurations.Add((IDisposable)config); return config; }
        public async Task Publish(Dictionary<string, string?> changes)
        {
            foreach (var pair in changes) _settings[pair.Key] = pair.Value;
            using var prepared = await Runtime.PrepareAsync(Build(), Ct);
            var metadata = new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(
                new JsonObject { ["EnrichmentConfigurationVersions"] = prepared!.EnrichmentVersions!.DeepClone() }))).Build();
            using var metadataLifetime = (IDisposable)metadata;
            foreach (var pair in metadata.AsEnumerable()) _settings[pair.Key] = pair.Value;
            prepared.Publish();
        }
        public async ValueTask DisposeAsync()
        { await Db.DisposeAsync(); _clients.Dispose(); foreach (var c in _configurations) c.Dispose(); Directory.Delete(_root, true); }
    }
    private sealed class Clock : TimeProvider
    { public long Now { get; set; } = 1000; public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now); }
    private sealed class Operations(IAudioUploadRepository repository, Store store, Clock clock) : IAudioUploadOperationFactory
    {
        public bool Enabled = true;
        public Operations? SettingsSource;
        public Task<AudioUploadRequestOperation> AcquireAsync(CancellationToken token) => Task.FromResult(new AudioUploadRequestOperation(
            new(SettingsSource?.Enabled ?? Enabled, 5 * 1024 * 1024, 60), new(repository, store,
                new(SettingsSource?.Enabled ?? Enabled, "audio", "audio/", 60), clock), new Noop()));
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
    private sealed class Store : IAudioObjectStore, IDisposable
    {
        public int Puts; public bool LosePutResponse; public int Disposals;
        public void Dispose() => Disposals++;
        public Action? AssertOutsideTransaction; public Func<Task>? BeforePut;
        public readonly Dictionary<string, byte[]> Objects = [];
        public async Task PutAsync(AudioObjectLocation location, Stream content, long length, string contentType, CancellationToken token)
        {
            AssertOutsideTransaction?.Invoke(); Puts++;
            if (BeforePut != null) await BeforePut();
            using var output = new MemoryStream(); await content.CopyToAsync(output, token); Objects[location.Key] = output.ToArray();
            if (LosePutResponse) throw new IOException("Synthetic lost response.");
        }
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => Task.FromResult(Objects.TryGetValue(location.Key, out var bytes) ? new AudioObjectInfo(bytes.Length, "audio/mpeg") : null);
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token) { Objects.Remove(location.Key); return Task.CompletedTask; }
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan ttl, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class StoreFactory(Store first) : IAudioObjectStoreFactory
    {
        public List<Store> Stores = [];
        public IAudioObjectStore Create(AudioOptions options)
        { var store = Stores.Count == 0 ? first : new Store(); Stores.Add(store); return store; }
    }
    private sealed class FailFieldPlanWrite : DbCommandInterceptor
    {
        public bool Enabled = true;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.StartsWith("UPDATE \"enrichment_round\"", StringComparison.Ordinal)
                && command.CommandText.Contains("plan_json", StringComparison.Ordinal)) throw new InvalidOperationException("Synthetic plan commit failure.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class HttpFactory(Handler handler) : IHttpClientFactory, IDisposable
    { private readonly HttpClient _client = new(handler); public HttpClient CreateClient(string name) => _client; public void Dispose() => _client.Dispose(); }
    private sealed class Handler : HttpMessageHandler
    {
        public int TextCalls; public int SpeechCalls;
        public string SpeechOutcome = "ready", PlanOutcome = "ready", Kind = "word";
        public bool Ipa; public bool OmitPhonetic; public bool OmitPartsOfSpeech; public byte[]? AudioBytes;
        public Action? AssertOutsideTransaction; public Func<Task>? BeforeTextResponse;
        public Func<CancellationToken, Task>? BeforeSpeechResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            AssertOutsideTransaction?.Invoke();
            if (message.RequestUri!.AbsolutePath.EndsWith("chat/completions", StringComparison.Ordinal))
            {
                TextCalls++;
                var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
                var request = JsonNode.Parse(body["messages"]![1]!["content"]!.GetValue<string>())!;
                var required = request["requiredPhonetic"]?.GetValue<string>();
                var plan = new
                {
                    certain = PlanOutcome != "uncertain",
                    entryKind = Kind,
                    classificationBasis = "Fixture classification",
                    spokenText = "lead",
                    pronunciationHint = Ipa ? "liːd" : "Use the requested reading.",
                    requiredPhonetic = required,
                    phonetic = OmitPhonetic ? null : "liːd",
                    partsOfSpeech = (new[] { new { meaningId = "meaning", partOfSpeech = "v." } }).Where(_ => !OmitPartsOfSpeech).ToArray(),
                    pronunciationControl = Ipa ? "ipa" : "instructions",
                    speechPhonetic = Ipa ? "liːd" : null
                };
                if (BeforeTextResponse != null) await BeforeTextResponse();
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        choices = new[] { new { finish_reason = "stop",
                    message = new { content = PlanOutcome == "invalid" ? "{}" : JsonSerializer.Serialize(plan) } } }
                    })
                };
            }
            SpeechCalls++;
            if (BeforeSpeechResponse != null) await BeforeSpeechResponse(token);
            if (SpeechOutcome == "throttle") return new(HttpStatusCode.TooManyRequests) { Content = JsonContent.Create(new { error = new { code = "rate_limit_exceeded" } }) };
            if (SpeechOutcome == "balance") return new(HttpStatusCode.TooManyRequests) { Content = JsonContent.Create(new { error = new { code = "insufficient_quota" } }) };
            if (SpeechOutcome == "service") return new(HttpStatusCode.ServiceUnavailable);
            if (message.RequestUri.AbsolutePath.EndsWith("t2a_v2", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { base_resp = new { status_code = 0 }, data = new { status = 2, audio = Convert.ToHexString(AudioBytes ?? Mp3) } }) };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(AudioBytes ?? Mp3) };
        }
    }
}
