using System.Data.Common;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    private static Task<EnrichmentAdminConfiguration> AdminConfig(Fixture f) =>
        new EnrichmentAdminConfigurationProvider(f.Runtime, f.Operations).CaptureAsync(Ct);

    [Fact]
    public async Task AdminReads_UnqueuedDisabledDemandsAndInheritedSpecifics_AreReadOnlyAndNeverCompleted()
    {
        await using var f = await Fixture.Create();
        await f.Publish(new() { ["Enrichment:FieldsEnabled"] = "false", ["Enrichment:AudioEnabled"] = "false" });
        var admin = new EnrichmentAdminRepository(f.Db); var c = await AdminConfig(f);
        var before = await Dump(f.Db);
        var summary = await admin.SummaryAsync(new(), c, f.Clock.Now, Ct);
        Assert.Equal(4, summary.ApplicableTargets); Assert.Equal(0, summary.CompletedTargets);
        Assert.Equal(4, summary.UnqueuedTargets); Assert.Equal(0, summary.PersistedTasks);
        Assert.Equal(new EnrichmentAdminCount("Waiting", "Disabled", 4), Assert.Single(summary.Targets));
        Assert.Empty((await admin.ListAsync(new(), c, f.Clock.Now, Ct)).Items);
        Assert.Null(await admin.DetailAsync("unknown", 1, 2, c, f.Clock.Now, Ct));
        Assert.Equal(before, await Dump(f.Db)); Assert.Equal(0, f.Http.TextCalls); Assert.Equal(0, f.Http.SpeechCalls);
        await f.Db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_us=char(9,160) WHERE id='meaning'", Ct);
        Assert.Equal(4, (await admin.SummaryAsync(new(), c, f.Clock.Now, Ct)).ApplicableTargets);
        // A nonempty specific target is required even when its IPA equals the
        // default. Empty other-accent specifics never add demands.
        await f.Db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='liːd' WHERE id='meaning'", Ct);
        summary = await admin.SummaryAsync(new(), c, f.Clock.Now, Ct);
        Assert.Equal(5, summary.ApplicableTargets); Assert.Equal(5, summary.UnqueuedTargets);
    }

    [Fact]
    public async Task AdminRead_ProjectsPartialSuccessSevenThrottlesAndExhaustionAfterServiceRecovery()
    {
        await using var f = await Fixture.Create(fields: false, shared: true);
        var uk = (await f.Run(Key()))!;
        var c = await AdminConfig(f); var admin = new EnrichmentAdminRepository(f.Db);
        var initial = await admin.SummaryAsync(new(Step: "Audio"), c, f.Clock.Now, Ct);
        Assert.Equal(1, initial.CompletedTargets); Assert.Equal(1, initial.MissingTargets);
        f.Http.SpeechOutcome = "throttle"; EnrichmentTask? us = null;
        for (var i = 0; i < 7; i++)
        {
            if (us != null) f.Clock.Now = Round(us).NextAttemptAtUnixMs!.Value;
            us = (await f.Run(Key(locale: AudioLocale.EnUs)))!;
        }
        var before = await Dump(f.Db);
        var detail = (await admin.DetailAsync(us!.Id, 1, 3, c, f.Clock.Now, Ct))!;
        Assert.Equal("Waiting", detail.Task.State); Assert.Equal("Throttled", detail.Task.WaitReason);
        Assert.Equal(7, detail.Task.Current.RequestCount); Assert.Equal(0, detail.Task.Current.ConsumedFailures);
        Assert.Equal(7, detail.Calls.TotalCount); Assert.Equal(3, detail.Calls.Items.Count);
        Assert.Equal(before, await Dump(f.Db));
        f.Http.SpeechOutcome = "service";
        for (var i = 0; i < 5; i++)
        { f.Clock.Now = Round(us).NextAttemptAtUnixMs!.Value; us = (await f.Run(Key(locale: AudioLocale.EnUs)))!; }
        f.Clock.Now += 600_000;
        // Successful independent service probe changes the gate, never item history.
        var gate = new EnrichmentServiceGate(new EnrichmentSchedulerRepository(f.Db, f.Tasks), f.Runtime, f.Clock);
        var admission = await gate.EnterAsync("tts", true, Ct);
        await gate.FinishAsync(admission, "Ready", null, true, null, Ct);
        detail = (await admin.DetailAsync(us.Id, 1, 100, c, f.Clock.Now, Ct))!;
        Assert.Equal("Failed", detail.Task.State); Assert.Equal(5, detail.Task.Current.ConsumedFailures);
        Assert.Equal(12, detail.Task.Current.RequestCount); Assert.Single(detail.Rounds.Items);
        Assert.Equal("Succeeded", (await admin.DetailAsync(uk.Id, 1, 1, c, f.Clock.Now, Ct))!.Task.State);
    }

    [Fact]
    public async Task AdminRetry_ConcurrentSameAndDifferentKeys_ReplaysAcrossRestartAndRetainsHistory()
    {
        await using var f = await Fixture.Create(fields: false);
        var task = await f.Ensure(Key());
        var guard = (await f.Tasks.ClaimAsync(task.Id, "fail", 1000, 9000, Ct))!;
        await f.Tasks.StopAsync(guard, "tts.capability_unsupported", 1000, Ct);
        var c = await AdminConfig(f); var key = Guid.NewGuid().ToString();
        Assert.Equal("provider.capability_unsupported", (await new EnrichmentAdminRepository(f.Db)
            .DetailAsync(task.Id, 1, 10, c, 1000, Ct))!.Task.Current.ErrorCode);
        await using var a = f.Open(); await using var b = f.Open();
        var first = new EnrichmentTaskRepository(a, f.Clock); var second = new EnrichmentTaskRepository(b, f.Clock);
        var same = await Task.WhenAll(first.RetryAsync(task.Id, 1, key, c, 1000, Ct), second.RetryAsync(task.Id, 1, key, c, 1000, Ct));
        Assert.Single(same, r => r.Status == EnrichmentRetryStatus.Started);
        Assert.Single(same, r => r.Status == EnrichmentRetryStatus.Replayed);
        Assert.All(same, r => Assert.Equal(2, r.Round));
        guard = (await first.ClaimAsync(task.Id, "fail-again", 1000, 9000, Ct))!;
        await first.StopAsync(guard, "tts.capability_unsupported", 1000, Ct);
        var different = await Task.WhenAll(first.RetryAsync(task.Id, 2, Guid.NewGuid().ToString(), c, 1000, Ct),
            second.RetryAsync(task.Id, 2, Guid.NewGuid().ToString(), c, 1000, Ct));
        Assert.Single(different, r => r.Status == EnrichmentRetryStatus.Started); Assert.Single(different, r => r.Status == EnrichmentRetryStatus.Conflict);
        await using var restart = f.Open(); var repo = new EnrichmentTaskRepository(restart, f.Clock);
        Assert.Equal(new(EnrichmentRetryStatus.Replayed, task.Id, 2), await repo.RetryAsync(task.Id, 1, key, c, 1000, Ct));
        Assert.Equal(EnrichmentRetryStatus.Conflict, (await repo.RetryAsync(task.Id, 3, Guid.NewGuid().ToString(), c, 1000, Ct)).Status);
        Assert.Equal(EnrichmentRetryStatus.NotFound, (await repo.RetryAsync("unknown", 1, Guid.NewGuid().ToString(), c, 1000, Ct)).Status);
        var detail = (await new EnrichmentAdminRepository(restart).DetailAsync(task.Id, 1, 2, c, 1000, Ct))!;
        Assert.Equal(3, detail.Rounds.TotalCount); Assert.Equal(new[] { 3, 2 }, detail.Rounds.Items.Select(r => r.Number));
        var old = (await new EnrichmentAdminRepository(restart).DetailAsync(task.Id, 2, 2, c, 1000, Ct))!;
        Assert.Equal("Failed", Assert.Single(old.Rounds.Items).State);
    }

    [Fact]
    public async Task AdminRetry_FieldsRefreshesSuccessfulPreparation_AndPrepareUnblocksOnlyFailedDependencies()
    {
        await using var f = await Fixture.Create();
        f.Http.OmitPhonetic = true; f.Http.OmitPartsOfSpeech = true;
        var fields = (await f.Run(Key(EnrichmentStep.Fields)))!;
        Assert.Equal("fields.candidates_missing", Round(fields).ErrorCode);
        var prepare = (await f.Ensure(Key(EnrichmentStep.Prepare)))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(prepare).State);
        var c = await AdminConfig(f);
        var retry = await f.Tasks.RetryAsync(fields.Id, fields.CurrentRound, Guid.NewGuid().ToString(), c, f.Clock.Now, Ct);
        Assert.Equal(EnrichmentRetryStatus.Started, retry.Status);
        var refreshed = (await f.Tasks.GetAsync(prepare.Id, Ct))!;
        Assert.Equal(2, refreshed.CurrentRound); Assert.Null(Round(refreshed).Plan);
        f.Http.OmitPhonetic = false; f.Http.OmitPartsOfSpeech = false;
        fields = (await f.Executor.ExecuteAsync(fields.Id, "retry-fields", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(fields).State); Assert.Equal(2, f.Http.TextCalls);
        Assert.Equal(3, fields.CurrentRound);
        // A retry cannot redo a completed field side.
        Assert.Equal(EnrichmentRetryStatus.Conflict, (await f.Tasks.RetryAsync(fields.Id, fields.CurrentRound, Guid.NewGuid().ToString(), c, f.Clock.Now, Ct)).Status);
        // Prepare failure and dependent audio failure are recovered together.
        var usPrepare = await f.Ensure(Key(EnrichmentStep.Prepare, AudioLocale.EnUs));
        var g = (await f.Tasks.ClaimAsync(usPrepare.Id, "fail", f.Clock.Now, f.Clock.Now + 10000, Ct))!;
        await f.Tasks.StopAsync(g, "text.invalid_plan", f.Clock.Now, Ct);
        var audio = (await f.Run(Key(locale: AudioLocale.EnUs)))!;
        Assert.Equal("preparation.failed", Round(audio).ErrorCode);
        Assert.Equal(EnrichmentRetryStatus.Started, (await f.Tasks.RetryAsync(usPrepare.Id, usPrepare.CurrentRound,
            Guid.NewGuid().ToString(), c, f.Clock.Now, Ct)).Status);
        Assert.Equal(2, (await f.Tasks.GetAsync(audio.Id, Ct))!.CurrentRound);
        Assert.Equal(fields.CurrentRound, (await f.Tasks.GetAsync(fields.Id, Ct))!.CurrentRound);
        Assert.Equal(EnrichmentState.Succeeded, Round((await f.Executor.ExecuteAsync(audio.Id, "retry-audio", Ct))!).State);
    }

    [Fact]
    public async Task AdminRetry_EquivalentCandidatesAlsoRefreshTheSuccessfulPreparationPlan()
    {
        await using var f = await Fixture.Create();
        f.Db.VocabularyMeanings.Add(new() { Id = "duplicate", VocabularyId = "word", BookId = "book", PartOfSpeech = "v.", Meaning = "guide" });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var fields = (await f.Run(Key(EnrichmentStep.Fields)))!;
        Assert.Equal("fields.equivalent_meaning", Round(fields).ErrorCode);
        var prepared = (await f.Ensure(Key(EnrichmentStep.Prepare)))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(prepared).State);
        var c = await AdminConfig(f);
        await f.Tasks.RetryAsync(fields.Id, fields.CurrentRound, Guid.NewGuid().ToString(), c, f.Clock.Now, Ct);
        var refreshed = (await f.Tasks.GetAsync(prepared.Id, Ct))!;
        Assert.Equal(prepared.CurrentRound + 1, refreshed.CurrentRound); Assert.Null(Round(refreshed).Plan);
        await f.Executor.ExecuteAsync(fields.Id, "new-plan", Ct);
        Assert.Equal(2, f.Http.TextCalls); // content still conflicts; fresh preparation was mandatory
    }

    [Fact]
    public async Task AdminRetry_TransactionFailureAndCancellationRollBackEveryRoundAndEpoch()
    {
        await using var f = await Fixture.Create(); f.Http.OmitPhonetic = true; f.Http.OmitPartsOfSpeech = true;
        var fields = (await f.Run(Key(EnrichmentStep.Fields)))!; var c = await AdminConfig(f);
        var before = await Dump(f.Db);
        await using (var failing = f.Open(new FailRetryPreparation()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new EnrichmentTaskRepository(failing, f.Clock)
                .RetryAsync(fields.Id, fields.CurrentRound, Guid.NewGuid().ToString(), c, f.Clock.Now, Ct));
        }
        Assert.Equal(before, await Dump(f.Db));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using (var failing = f.Open(new CancelRetry(cancel)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EnrichmentTaskRepository(failing, f.Clock)
                .RetryAsync(fields.Id, fields.CurrentRound, Guid.NewGuid().ToString(), c, f.Clock.Now, cancel.Token));
        }
        Assert.Equal(before, await Dump(f.Db));
    }

    [Fact]
    public async Task AdminPaging_UsesOneSnapshotWhenAWriterChangesTasksBetweenCountAndPage()
    {
        await using var f = await Fixture.Create(fields: false);
        await f.Ensure(Key()); await f.Ensure(Key(locale: AudioLocale.EnUs));
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", Ct);
        var c = await AdminConfig(f); var fired = false;
        await using var reading = f.Open(new AfterAdminCount(async () =>
        {
            fired = true;
            await using var writer = f.Open();
            await new UnitOfWork(writer).ExecuteInTransactionAsync(async () =>
            { return await writer.Database.ExecuteSqlRawAsync("DELETE FROM enrichment_target", Ct); }, Ct);
        }));
        var page = await new EnrichmentAdminRepository(reading).ListAsync(new(Size: 1), c, f.Clock.Now, Ct);
        Assert.True(fired); Assert.Equal(2, page.TotalCount); Assert.Single(page.Items);
        Assert.Equal("en-GB", page.Items[0].Locale);
        Assert.Equal(0, (await new EnrichmentAdminRepository(f.Db).ListAsync(new(), c, f.Clock.Now, Ct)).TotalCount);
    }

    [Fact]
    public async Task AdminRead_CleanupFailureAndPublishedReplacementRemainIndependent_AndClearRestoresInheritance()
    {
        await using var f = await Fixture.Create(fields: false);
        var edits = new MeaningPhoneticRepository(f.Db, new AudioInvalidationRepository(f.Db, f.Clock), f.Tasks);
        async Task Edit(string? value)
        {
            var before = await edits.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Ct);
            await new Lexarbor.Domain.Services.MeaningPhoneticEditService(edits, new UnitOfWork(f.Db)).ApplyAsync(
                [new("book", "word", "meaning", AudioLocale.EnGb, true, value, before)],
                new(f.Runtime.Current.TextVersion, f.Runtime.Current.UkTtsVersion, f.Runtime.Current.UsTtsVersion), f.Clock.Now, Ct);
        }
        await f.Db.Vocabularies.ExecuteUpdateAsync(set => set.SetProperty(w => w.PhoneticUk, "liːd"), Ct);
        await f.Run(Key()); // completed default supports same-IPA fallback
        await Edit("liːd");
        var c = await AdminConfig(f); var admin = new EnrichmentAdminRepository(f.Db);
        var specific = (await f.Ensure(Key(meaning: "meaning")))!;
        var fallback = (await admin.DetailAsync(specific.Id, 1, 10, c, f.Clock.Now, Ct))!;
        Assert.True(fallback.Task.Missing); // effective default is not own success
        await f.Executor.ExecuteAsync(specific.Id, "first", Ct);
        f.Http.OmitPhonetic = true;
        await Edit("lɛd");
        var old = await f.Db.AudioInvalidations.AsNoTracking().SingleAsync(Ct);
        var invalidations = new AudioInvalidationRepository(f.Db, f.Clock);
        var claimed = (await invalidations.ClaimAsync(old.AssetId, "cleanup", f.Clock.Now, f.Clock.Now + 10000, f.Clock.Now + 1000, Ct))!;
        await invalidations.ReleaseAsync(old.AssetId, claimed.Lease, f.Clock.Now, f.Clock.Now + 60000, Ct);
        await f.Executor.ExecuteAsync(specific.Id, "replacement", Ct);
        var beforeDump = await Dump(f.Db);
        var detail = (await admin.DetailAsync(specific.Id, 1, 10, c, f.Clock.Now, Ct))!;
        Assert.Equal("Succeeded", detail.Task.State); Assert.False(detail.Task.Missing);
        Assert.Equal(1, detail.Task.Cleanup.Waiting); Assert.Equal(1, detail.Task.Cleanup.Attempts);
        Assert.Equal("audio.invalidation_failed", detail.Task.Cleanup.ErrorCode);
        Assert.Single(detail.Cleanup.Items); Assert.Equal(beforeDump, await Dump(f.Db));
        var json = JsonSerializer.Serialize(detail);
        Assert.DoesNotContain(old.AssetId, json); Assert.DoesNotContain("\"Bucket\"", json); Assert.DoesNotContain(old.ObjectKey, json);
        await Edit(null);
        detail = (await admin.DetailAsync(specific.Id, 1, 10, c, f.Clock.Now, Ct))!;
        Assert.True(detail.Task.InheritsDefault); Assert.False(detail.Task.Applicable); Assert.False(detail.Task.Missing);
        Assert.Equal("Cancelled", detail.Task.State); Assert.Equal(2, detail.Task.Cleanup.Waiting);
        var summary = await admin.SummaryAsync(new(MeaningId: "meaning"), c, f.Clock.Now, Ct);
        Assert.Equal(0, summary.ApplicableTargets); Assert.Equal(0, summary.CompletedTargets);
        Assert.Equal(EnrichmentRetryStatus.Conflict, (await f.Tasks.RetryAsync(specific.Id, detail.Task.Current.Number,
            Guid.NewGuid().ToString(), c, f.Clock.Now, Ct)).Status);
    }

    private sealed class FailRetryPreparation : DbCommandInterceptor
    {
        private int _updates;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"enrichment_target\"", StringComparison.Ordinal) && ++_updates == 2)
                throw new InvalidOperationException("Synthetic retry dependency failure.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CancelRetry(CancellationTokenSource source) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"enrichment_target\"", StringComparison.Ordinal)) source.Cancel();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class AfterAdminCount(Func<Task> write) : DbCommandInterceptor
    {
        private bool _done;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken token = default)
        {
            if (!_done && command.CommandText.StartsWith("SELECT COUNT(*)", StringComparison.Ordinal)) { _done = true; await write(); }
            return result;
        }
    }
    internal static async Task<string> Dump(VocabularyDbContext db)
    {
        await db.Database.OpenConnectionAsync(Ct);
        try
        {
            var connection = db.Database.GetDbConnection();
            var names = new List<string>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                await using var r = await cmd.ExecuteReaderAsync(Ct); while (await r.ReadAsync(Ct)) names.Add(r.GetString(0));
            }
            var data = new List<object>();
            foreach (var name in names)
            {
                await using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
                await using var r = await cmd.ExecuteReaderAsync(Ct); var rows = new List<string>();
                while (await r.ReadAsync(Ct)) { var values = new object[r.FieldCount]; r.GetValues(values); rows.Add(JsonSerializer.Serialize(values)); }
                data.Add(new { name, rows = rows.Order(StringComparer.Ordinal).ToArray() });
            }
            return JsonSerializer.Serialize(data);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
