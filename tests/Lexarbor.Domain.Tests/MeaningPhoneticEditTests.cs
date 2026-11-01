using System.Data.Common;
using System.Text.Json;
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

public sealed class MeaningPhoneticEditTests
{
    private static CancellationToken Token => AudioTestDatabase.Token;
    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }
    private static async Task Init(VocabularyDbContext db)
    {
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
        await db.VocabularyMeanings.Where(row => row.Id == "meaning").ExecuteUpdateAsync(set => set
            .SetProperty(row => row.PhoneticUk, "A").SetProperty(row => row.PhoneticUs, "US"), Token);
        db.ChangeTracker.Clear();
    }
    private static MeaningPhoneticRepository Repo(VocabularyDbContext db, Clock clock) =>
        new(db, new AudioInvalidationRepository(db, clock), new EnrichmentTaskRepository(db, clock));
    private static MeaningPhoneticEditService Service(VocabularyDbContext db, Clock clock) => new(Repo(db, clock), new UnitOfWork(db));
    private static EnrichmentTargetKey Key(EnrichmentStep step = EnrichmentStep.Audio, AudioLocale locale = AudioLocale.EnGb) => new("word", "meaning", locale, step);
    private static async Task<MeaningPhoneticEdit> Edit(VocabularyDbContext db, Clock clock, string? value, AudioLocale locale = AudioLocale.EnGb) =>
        new("book", "word", "meaning", locale, true, value, await Repo(db, clock).ReadAsync("book", "word", "meaning", locale, Token));
    private static async Task<MeaningPhoneticEditResult> Apply(VocabularyDbContext db, Clock clock, string? value, AudioLocale locale = AudioLocale.EnGb) =>
        Assert.Single(await Service(db, clock).ApplyAsync([await Edit(db, clock, value, locale)], new("config", "config", "config"), 10, Token));
    private static async Task<AudioStoredUpload> Reserve(VocabularyDbContext db, Clock clock, string? meaning = "meaning", AudioLocale locale = AudioLocale.EnGb,
        long expires = 1000)
    {
        var selection = AudioSelectionService.Select(await new AudioReadRepository(db).GetSnapshotAsync("word", meaning, Token), locale);
        var request = new AudioUploadRequest(Guid.NewGuid().ToString(), AudioTestDatabase.Hash, "word", meaning, locale,
            selection.SourceRevision, selection.OwnBinding?.Asset.Id, AudioTestDatabase.Hash, 128, "test-audio");
        var uploads = new AudioUploadRepository(db, clock);
        var pending = Assert.IsType<AudioStoredUpload>((await uploads.ReserveAsync(request, "uploader", 1, expires, Token)).Upload);
        Assert.True(await uploads.BeginTransferAsync(request.IdempotencyKey, pending.Operation.UploadLease, 2, Token));
        return pending;
    }
    private static async Task<AudioStoredUpload> Publish(VocabularyDbContext db, Clock clock, string? meaning = "meaning", AudioLocale locale = AudioLocale.EnGb)
    {
        var pending = await Reserve(db, clock, meaning, locale);
        Assert.Equal(200, (await new AudioUploadRepository(db, clock).CompleteAsync(pending.Operation.IdempotencyKey, pending.Operation.UploadLease, 3, Token))!.Receipt!.StatusCode);
        return (await new AudioUploadRepository(db, clock).GetAsync(pending.Operation.IdempotencyKey, Token))!;
    }

    [Theory]
    [InlineData(AudioLocale.EnGb)]
    [InlineData(AudioLocale.EnUs)]
    public async Task Change_UnbindsOnlyOwnAccentAndPersistsExactAuthorityAndBothSteps(AudioLocale locale)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db); var clock = new Clock();
        var own = await Publish(db, clock, locale: locale);
        var otherLocale = locale == AudioLocale.EnGb ? AudioLocale.EnUs : AudioLocale.EnGb;
        var otherAccent = await Publish(db, clock, locale: otherLocale); var baseline = await Publish(db, clock, null, locale);
        db.VocabularyMeanings.Add(new() { Id = "second", BookId = "book", VocabularyId = "word", Meaning = "another", PhoneticUk = "A", PhoneticUs = "US" });
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        var second = await Publish(db, clock, "second", locale);
        var result = await Apply(db, clock, locale == AudioLocale.EnGb ? "uk" : "us", locale);
        Assert.True(result.Changed); Assert.Equal(1, result.Current.Epoch); Assert.Null(result.Current.OwnAssetId);
        var selected = AudioSelectionService.Select(await new AudioReadRepository(db).GetSnapshotAsync("word", "meaning", Token), locale);
        Assert.Equal(baseline.Asset.Id, selected.EffectiveBinding!.Asset.Id); // fallback does not complete the own target
        Assert.Equal(otherAccent.Asset.Id, AudioSelectionService.Select(await new AudioReadRepository(db).GetSnapshotAsync("word", "meaning", Token), otherLocale).OwnBinding!.Asset.Id);
        Assert.Equal(second.Asset.Id, AudioSelectionService.Select(await new AudioReadRepository(db).GetSnapshotAsync("word", "second", Token), locale).OwnBinding!.Asset.Id);
        var authority = (await new AudioInvalidationRepository(db, clock).GetAsync(own.Asset.Id, Token))!;
        Assert.Equal("meaning", authority.MeaningId); Assert.Equal(locale, authority.Locale); Assert.Equal(1, authority.AuthorizationEpoch);
        foreach (var step in new[] { EnrichmentStep.Prepare, EnrichmentStep.Audio })
        {
            var target = await db.EnrichmentTargets.SingleAsync(row => row.MeaningId == "meaning" && row.Locale == locale && row.Step == step, Token);
            var task = (await new EnrichmentTaskRepository(db, clock).GetAsync(target.Id, Token))!;
            Assert.Equal(EnrichmentState.Pending, Assert.Single(task.Rounds).State);
        }
        Assert.Equal(2, await db.EnrichmentTargets.CountAsync(Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAndPerAccentConfiguration_PersistWorkWithoutWaitingForReadiness(bool missing)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db); var clock = new Clock();
        var uk = await Edit(db, clock, "B"); var us = await Edit(db, clock, "new-us", AudioLocale.EnUs);
        await Service(db, clock).ApplyAsync([uk, us], missing ? new(null, null, null) : new("text-v", "uk-v", "us-v"), 10, Token);
        foreach (var target in await db.EnrichmentTargets.ToListAsync(Token))
        {
            var round = await db.EnrichmentRounds.SingleAsync(row => row.TargetId == target.Id, Token);
            Assert.Equal(EnrichmentState.Pending, round.State); Assert.Equal(0, round.RequestCount);
            Assert.Equal(missing ? "configuration.missing" : target.Step == EnrichmentStep.Prepare ? "text-v"
                : target.Locale == AudioLocale.EnGb ? "uk-v" : "us-v", round.ConfigurationVersion);
        }
        Assert.Equal(4, await db.EnrichmentTargets.CountAsync(Token));
    }

    [Fact]
    public async Task PresenceNormalizationClearAndRepetition_PreserveHistoryWithoutDuplicateWork()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db); var clock = new Clock();
        var baseline = await Publish(db, clock, null); var own = await Publish(db, clock); var repo = Repo(db, clock);
        var absent = await Edit(db, clock, "B");
        Assert.False(Assert.Single(await Service(db, clock).ApplyAsync([absent with { IsProvided = false }], new("config", "config", "config"), 10, Token)).Changed);
        Assert.False((await Apply(db, clock, " [ A ] ")).Changed);
        Assert.Empty(await db.EnrichmentTargets.ToListAsync(Token)); Assert.Empty(await db.AudioInvalidations.ToListAsync(Token));
        Assert.Equal("A", (await repo.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Token)).Value);
        Assert.True((await Apply(db, clock, "é")).Changed);
        Assert.False((await Apply(db, clock, "/e\u0301/")).Changed);
        var tasks = new EnrichmentTaskRepository(db, clock);
        var audio = await tasks.EnsureAsync(Key(), "config", 11, Token);
        var guard = (await tasks.ClaimAsync(audio!.Id, "worker", 12, 100, Token))!;
        Assert.True(await tasks.SavePlanAsync(guard, new("apple", null, "é", null), 13, Token));
        Assert.True((await Apply(db, clock, null)).Changed);
        Assert.Equal(baseline.Asset.Id, AudioSelectionService.Select(await new AudioReadRepository(db).GetSnapshotAsync("word", "meaning", Token), AudioLocale.EnGb).EffectiveBinding!.Asset.Id);
        Assert.Equal(2, (await repo.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Token)).Epoch);
        Assert.False((await Apply(db, clock, "  ")).Changed);
        Assert.Equal(EnrichmentState.Cancelled, (await tasks.GetAsync(audio.Id, Token))!.Rounds.Last().State);
        Assert.NotNull((await tasks.GetAsync(audio.Id, Token))!.Rounds.Last().Plan);
        Assert.Null(await tasks.ClaimAsync(audio.Id, "late", 14, 100, Token));
        Assert.False(await new UnitOfWork(db).ExecuteInTransactionAsync(() => tasks.ValidateInTransactionAsync(guard, 14, Token), Token));
        Assert.Single(await db.AudioInvalidations.ToListAsync(Token));
        Assert.Equal(own.Asset.Id, (await new AudioInvalidationRepository(db, clock).GetAsync(own.Asset.Id, Token))!.AssetId);
        Assert.True((await Apply(db, clock, "A")).Changed); // blank to nonblank opens a fresh round
        Assert.Equal(3, (await repo.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Token)).Epoch);
        Assert.Equal(EnrichmentState.Pending, (await tasks.GetAsync(audio.Id, Token))!.Rounds.Last().State);
        var legacyModel = (await new VocabularyMeaningRepository(db).GetByIdAsync("meaning"))!;
        legacyModel.PhoneticUk = "bypass"; legacyModel.PhoneticUs = null;
        await new VocabularyMeaningRepository(db).UpdateAsync(legacyModel); await db.SaveChangesAsync(Token);
        Assert.Equal("A", (await repo.ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Token)).Value);
        Assert.Equal("US", (await repo.ReadAsync("book", "word", "meaning", AudioLocale.EnUs, Token)).Value);
    }

    [Fact]
    public async Task Aba_RejectsOldUploadAndBothTaskGuardsEvenWithIdenticalContentAndNoOwnBinding()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db); var clock = new Clock();
        var tasks = new EnrichmentTaskRepository(db, clock); var guards = new List<EnrichmentExecutionGuard>();
        foreach (var step in new[] { EnrichmentStep.Prepare, EnrichmentStep.Audio })
        {
            var task = await tasks.EnsureAsync(Key(step), "config", 1, Token);
            guards.Add((await tasks.ClaimAsync(task!.Id, "worker", 2, 1000, Token))!);
        }
        var pending = await Reserve(db, clock); var original = await Edit(db, clock, "B");
        await Service(db, clock).ApplyAsync([original], new("config", "config", "config"), 10, Token); await Apply(db, clock, "A");
        var current = await Repo(db, clock).ReadAsync("book", "word", "meaning", AudioLocale.EnGb, Token);
        Assert.Equal(original.Expected.SourceRevision, current.SourceRevision); Assert.Equal(original.Expected.OwnAssetId, current.OwnAssetId);
        Assert.Equal(2, current.Epoch);
        foreach (var guard in guards)
        {
            Assert.False(await new UnitOfWork(db).ExecuteInTransactionAsync(() => tasks.ValidateInTransactionAsync(guard, 20, Token), Token));
            Assert.NotEqual(guard.Input.Revision, (await tasks.ReadInputAsync(Key(guard == guards[0] ? EnrichmentStep.Prepare : EnrichmentStep.Audio), Token))!.Revision);
        }
        var rejected = await new AudioUploadRepository(db, clock).CompleteAsync(pending.Operation.IdempotencyKey, pending.Operation.UploadLease, 20, Token);
        Assert.Equal(409, rejected!.Receipt!.StatusCode); Assert.Null((await new AudioUploadRepository(db, clock).GetAsync(pending.Operation.IdempotencyKey, Token))!.Asset.FirstPublishedAtUnixMs);
        await Assert.ThrowsAsync<ConflictException>(() => Service(db, clock).ApplyAsync([original], new("config", "config", "config"), 20, Token));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("execution-expired")]
    [InlineData("upload-expired")]
    [InlineData("wrong-target")]
    public async Task GuardedPublication_ValidatesBothIndependentLeasesInsideActualWriter(string scenario)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db); var clock = new Clock();
        var tasks = new EnrichmentTaskRepository(db, clock); var task = await tasks.EnsureAsync(Key(), "config", 1, Token);
        var guard = (await tasks.ClaimAsync(task!.Id, "worker", 2, scenario == "execution-expired" ? 10 : 1000, Token))!;
        var pending = await Reserve(db, clock, expires: scenario == "upload-expired" ? 10 : 1000);
        if (scenario == "wrong-target")
        {
            var wrong = await tasks.EnsureAsync(new("word", null, AudioLocale.EnGb, EnrichmentStep.Audio), "config", 1, Token);
            guard = (await tasks.ClaimAsync(wrong!.Id, "other", 2, 1000, Token))!;
        }
        var result = await new AudioUploadRepository(db, clock).CompleteGuardedAsync(pending.Operation.IdempotencyKey, pending.Operation.UploadLease, guard, 10, Token);
        if (scenario == "upload-expired") Assert.Null(result);
        else Assert.Equal(scenario == "valid" ? 200 : 409, result!.Receipt!.StatusCode);
        Assert.Equal(scenario == "valid" ? 1 : 0, await db.VocabularyAudioBindings.CountAsync(Token));
    }

    [Fact]
    public async Task QueuedPublication_RechecksAfterAnEditCommitsRatherThanTrustingEarlierGuardValidation()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db); var clock = new Clock();
        var tasks = new EnrichmentTaskRepository(db, clock); var task = await tasks.EnsureAsync(Key(), "config", 1, Token);
        var guard = (await tasks.ClaimAsync(task!.Id, "worker", 2, 1000, Token))!; var pending = await Reserve(db, clock);
        Assert.True(await new UnitOfWork(db).ExecuteInTransactionAsync(() => tasks.ValidateInTransactionAsync(guard, 3, Token), Token));
        await using var editor = file.Open(); var edit = await Edit(editor, clock, "B");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing = new UnitOfWork(editor).ExecuteInTransactionAsync(async () =>
        { entered.SetResult(); await release.Task; await Service(editor, clock).ApplyAsync([edit], new("config", "config", "config"), 10, Token); return 0; }, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var publishing = new AudioUploadRepository(db, clock).CompleteGuardedAsync(pending.Operation.IdempotencyKey, pending.Operation.UploadLease, guard, 20, Token);
        release.SetResult(); await writing;
        Assert.Equal(409, (await publishing)!.Receipt!.StatusCode);
        Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(Token));
    }

    [Theory]
    [InlineData("INSERT INTO \"audio_invalidation\"")]
    [InlineData("INSERT INTO \"meaning_phonetic_epoch\"")]
    [InlineData("UPDATE \"vocabulary_meaning\"")]
    [InlineData("DELETE FROM \"vocabulary_audio_binding\"")]
    [InlineData("INSERT INTO \"enrichment_target\"")]
    [InlineData("INSERT INTO \"enrichment_round\"")]
    public async Task FailureAtEachMutation_RollsBackAllStateAndRetryUsesFreshConditions(string command)
    {
        using var file = new AudioTestDatabase(); var fault = new Failure(command); var clock = new Clock();
        await using var db = file.Open(fault); await Init(db); await Publish(db, clock);
        var edit = await Edit(db, clock, "B"); var before = await Rows(file); fault.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Service(db, clock).ApplyAsync([edit], new("config", "config", "config"), 10, Token));
        Assert.True(fault.Triggered); Assert.Equal(before, await Rows(file));
        Assert.True(Assert.Single(await Service(db, clock).ApplyAsync([edit], new("config", "config", "config"), 10, Token)).Changed);
    }

    [Theory]
    [InlineData("UPDATE \"enrichment_round\"", 0)]
    [InlineData("INSERT INTO \"enrichment_round\"", 1)]
    public async Task FailedInvalidation_RestoresOldPlansGuardsAndEpochAfterEarlierStepWrites(string pattern, int skip)
    {
        using var file = new AudioTestDatabase(); var clock = new Clock(); var fault = new Failure(pattern, skip);
        await using var db = file.Open(fault); await Init(db); await Apply(db, clock, "B");
        var tasks = new EnrichmentTaskRepository(db, clock); var guards = new List<EnrichmentExecutionGuard>();
        foreach (var step in new[] { EnrichmentStep.Prepare, EnrichmentStep.Audio })
        {
            var task = (await tasks.EnsureAsync(Key(step), "config", 10, Token))!;
            var guard = (await tasks.ClaimAsync(task.Id, "worker", 11, 1000, Token))!; guards.Add(guard);
            Assert.True(await tasks.SavePlanAsync(guard, new("apple", "bounded", "B", null), 12, Token));
        }
        var before = await Rows(file); var edit = await Edit(db, clock, "C"); fault.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Service(db, clock).ApplyAsync([edit], new("config", "config", "config"), 20, Token));
        Assert.True(fault.Triggered); Assert.Equal(before, await Rows(file));
        foreach (var guard in guards)
            Assert.True(await new UnitOfWork(db).ExecuteInTransactionAsync(() => tasks.ValidateInTransactionAsync(guard, 21, Token), Token));
    }

    [Fact]
    public async Task SecondAccentFailureAndCancellation_RollBackFirstAccent_AndWrongWriterOrOwnershipCannotEdit()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db); var clock = new Clock();
        var uk = await Edit(db, clock, "B"); var us = await Edit(db, clock, "other", AudioLocale.EnUs); var before = await Rows(file);
        await Assert.ThrowsAsync<ConflictException>(() => Service(db, clock).ApplyAsync([uk, us with { Expected = us.Expected with { Epoch = 999 } }], new("config", "config", "config"), 10, Token));
        Assert.Equal(before, await Rows(file));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(db, clock).ApplyAsync([uk], new("config", "config", "config"), 10, cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repo(db, clock).UpdateInTransactionAsync(uk, new("config", "config", "config"), 10, Token));
        await using var other = file.Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UnitOfWork(other).ExecuteInTransactionAsync(() => Repo(db, clock).UpdateInTransactionAsync(uk, new("config", "config", "config"), 10, Token), Token));
        await Assert.ThrowsAsync<ConflictException>(() => Service(db, clock).ApplyAsync([uk with { WordId = "other" }], new("config", "config", "config"), 10, Token));
        Assert.Equal(before, await Rows(file));
    }

    [Fact]
    public async Task ConcurrentEdits_OneWinnerAndSharedAssetAuthorityIsRetainedWithoutOverwritingOriginalTarget()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Init(db); var clock = new Clock();
        var old = await Publish(db, clock);
        db.VocabularyMeanings.Add(new() { Id = "second", BookId = "book", VocabularyId = "word", Meaning = "second", PhoneticUs = "S" });
        db.VocabularyAudioBindings.Add(new() { Id = "shared", WordId = "word", MeaningId = "second", Locale = AudioLocale.EnUs, AssetId = old.Asset.Id, SourceRevision = AudioTestDatabase.Hash });
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        var edit = await Edit(db, clock, "B"); await using var other = file.Open();
        async Task<bool> TryEdit(VocabularyDbContext scope, string value)
        { try { await Service(scope, clock).ApplyAsync([edit with { Value = value }], new("config", "config", "config"), 10, Token); return true; } catch (ConflictException) { return false; } }
        var results = await Task.WhenAll(TryEdit(db, "B"), TryEdit(other, "C")); Assert.Single(results, value => value);
        var auth = (await new AudioInvalidationRepository(db, clock).GetAsync(old.Asset.Id, Token))!;
        Assert.Null(await new AudioInvalidationRepository(db, clock).ClaimAsync(old.Asset.Id, "delete", 10, 100, 20, Token));
        var second = await Repo(db, clock).ReadAsync("book", "word", "second", AudioLocale.EnUs, Token);
        await Service(db, clock).ApplyAsync([new("book", "word", "second", AudioLocale.EnUs, true, "T", second)], new("config", "config", "config"), 20, Token);
        var after = (await new AudioInvalidationRepository(db, clock).GetAsync(old.Asset.Id, Token))!;
        Assert.Equal(auth.MeaningId, after.MeaningId); Assert.Equal(auth.AuthorizationEpoch, after.AuthorizationEpoch);
        Assert.NotNull(await new AudioInvalidationRepository(db, clock).ClaimAsync(old.Asset.Id, "delete", 20, 100, 30, Token));
    }

    [Fact]
    public async Task RestartAndDisabledStorage_PreserveWork_AndDeleteFailureDoesNotBlockIndependentNewPublication()
    {
        using var file = new AudioTestDatabase(); var clock = new Clock(); var store = new Store(); AudioStoredUpload old; string id;
        await using (var db = file.Open())
        {
            await Init(db); old = await Publish(db, clock); store.Objects.Add(old.Asset.Location);
            await Apply(db, clock, "B"); var tasks = new EnrichmentTaskRepository(db, clock);
            var task = (await tasks.EnsureAsync(Key(), "config", 10, Token))!; id = task.Id;
            var guard = (await tasks.ClaimAsync(id, "worker", 11, 1000, Token))!;
            Assert.True(await tasks.WaitAsync(guard, EnrichmentWaitReason.Disabled, 12, 20, Token));
            var disabled = Recovery(db, clock, store, false); Assert.Equal(0, (await disabled.RunBatchAsync(Token)).Examined); Assert.Equal(0, store.Removes);
        }
        clock.Now = 20;
        await using (var restarted = file.Open())
        {
            var tasks = new EnrichmentTaskRepository(restarted, clock); var persisted = (await tasks.GetAsync(id, Token))!;
            Assert.Equal(EnrichmentWaitReason.Disabled, persisted.Rounds.Last().WaitReason);
            Assert.Single((await tasks.EnsureAsync(Key(), "config", 20, Token))!.Rounds);
            Assert.NotNull(await tasks.ClaimAsync(id, "new-worker", 20, 1000, Token));
            store.Fail = true; Assert.Equal(1, (await Recovery(restarted, clock, store, true).RunBatchAsync(Token)).Failed);
            var fresh = await Publish(restarted, clock); Assert.NotEqual(old.Asset.Id, fresh.Asset.Id);
            Assert.Null(await new AudioInvalidationRepository(restarted, clock).GetAsync(fresh.Asset.Id, Token));
            var waiting = (await new AudioInvalidationRepository(restarted, clock).GetAsync(old.Asset.Id, Token))!;
            clock.Now = waiting.NextAttemptAtUnixMs; store.Fail = false;
            Assert.Equal(1, (await Recovery(restarted, clock, store, true).RunBatchAsync(Token)).Observed);
            Assert.Equal(fresh.Asset.Id, AudioSelectionService.Select(await new AudioReadRepository(restarted).GetSnapshotAsync("word", "meaning", Token), AudioLocale.EnGb).OwnBinding!.Asset.Id);
            Assert.Equal(AudioReservationDisposition.Replay, (await new AudioUploadRepository(restarted, clock).ReserveAsync(
                new(old.Operation.IdempotencyKey, old.Operation.RequestFingerprint, "word", "meaning", AudioLocale.EnGb,
                    old.Operation.SourceRevision, null, AudioTestDatabase.Hash, 128, "test-audio"), "replay", clock.Now, clock.Now + 1000, Token)).Disposition);
        }
    }
    private static AudioInvalidationRecoveryService Recovery(VocabularyDbContext db, Clock clock, Store store, bool enabled) => new(new AudioInvalidationRepository(db, clock), store,
        new(enabled, "test-audio", "audio/", BatchSize: 10, RetrySeconds: 1), clock);
    private sealed class Store : IAudioObjectStore
    {
        public HashSet<AudioObjectLocation> Objects { get; } = []; public bool Fail; public int Removes;
        public Task RemoveAsync(AudioObjectLocation location, CancellationToken token)
        { Removes++; if (Fail) throw new IOException("Synthetic delete failure."); Objects.Remove(location); return Task.CompletedTask; }
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => Task.FromResult(Objects.Contains(location) ? new AudioObjectInfo(128, "audio/mpeg") : null);
        public Task PutAsync(AudioObjectLocation location, Stream content, long length, string contentType, CancellationToken token) => throw new NotSupportedException();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => throw new NotSupportedException();
    }
    private static async Task<string> Rows(AudioTestDatabase file) => JsonSerializer.Serialize(await file.RowsAsync("""
        SELECT id,phonetic_uk,phonetic_us,updated_at FROM vocabulary_meaning ORDER BY id
        """)) + JsonSerializer.Serialize(await file.RowsAsync("SELECT * FROM vocabulary_audio_binding ORDER BY id"))
        + JsonSerializer.Serialize(await file.RowsAsync("SELECT * FROM meaning_phonetic_epoch"))
        + JsonSerializer.Serialize(await file.RowsAsync("SELECT * FROM audio_invalidation"))
        + JsonSerializer.Serialize(await file.RowsAsync("SELECT * FROM enrichment_target"))
        + JsonSerializer.Serialize(await file.RowsAsync("SELECT * FROM enrichment_round"));
    private sealed class Failure(string pattern, int skip = 0) : DbCommandInterceptor
    {
        public bool Armed; public bool Triggered;
        private void Check(DbCommand command)
        { if (Armed && command.CommandText.Contains(pattern, StringComparison.Ordinal) && skip-- <= 0) { Armed = false; Triggered = true; throw new InvalidOperationException("Injected transaction failure."); } }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Check(command); return ValueTask.FromResult(result); }
    }
}
