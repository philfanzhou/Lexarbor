using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class AudioInvalidationTests
{
    private static CancellationToken Token => AudioTestDatabase.Token;

    [Fact]
    public async Task ExactSpecificAuthority_DeletesPublishedObjectAndRetainsReceiptAcrossRestart()
    {
        using var f = new Fixture(); AudioStoredUpload old; AudioUploadReceipt receipt;
        await using (var db = f.File.Open())
        {
            await Init(db); old = await Publish(db, f.Clock, "meaning");
            receipt = (await new AudioUploadRepository(db, f.Clock).GetAsync(old.Operation.IdempotencyKey, Token))!.Operation.Receipt!;
            f.Store.Objects.Add(old.Asset.Location); await AuthorizeAndUnbind(db, f.Clock, old.Asset.Id);
        }
        await using (var db = f.File.Open())
        {
            Assert.Equal(1, (await f.Service(db).RunBatchAsync(Token)).Observed);
            Assert.Empty(f.Store.Objects);
            var retained = (await new AudioInvalidationRepository(db, f.Clock).GetAsync(old.Asset.Id, Token))!;
            Assert.Equal(AudioInvalidationState.Observed, retained.State); Assert.NotNull(retained.DeleteStartedAtUnixMs);
            var uploads = new AudioUploadRepository(db, f.Clock);
            var replay = await uploads.ReserveAsync(Request(old.Operation.IdempotencyKey, "meaning"), "retry", 10, 1000, Token);
            Assert.Equal(AudioReservationDisposition.Replay, replay.Disposition); Assert.Equal(receipt, replay.Upload!.Operation.Receipt);
            Assert.Null(await uploads.CompleteAsync(old.Operation.IdempotencyKey, old.Operation.UploadLease, 10, Token));
            Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(Token)); Assert.True(await new AudioRecordExistenceRepository(db).HasAnyAsync(Token));
            Assert.Equal(0, (await f.Service(db).RunBatchAsync(Token)).Examined);
            f.Clock.Now = retained.NextAttemptAtUnixMs;
            Assert.Equal(1, (await f.Service(db).RunBatchAsync(Token)).Observed); // repeated Remove / absent Stat
            Assert.Equal(2, f.Store.Removes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAndOtherReferencesWait_AndUnapprovedHistoricalObjectsRemain(bool defaultReference)
    {
        using var f = new Fixture(); await using var db = f.File.Open(); await Init(db);
        var specific = await Publish(db, f.Clock, "meaning"); var historical = await Publish(db, f.Clock, null, AudioLocale.EnUs);
        await new AudioUploadRepository(db, f.Clock).UnbindAsync("word", null, AudioLocale.EnUs, historical.Asset.Id, Token);
        f.Store.Objects.UnionWith([specific.Asset.Location, historical.Asset.Location]);
        var reference = new VocabularyAudioBindingEntity
        {
            Id = "reference",
            WordId = defaultReference ? "word" : "other",
            MeaningId = defaultReference ? null : "other-meaning",
            Locale = AudioLocale.EnGb,
            AssetId = specific.Asset.Id,
            SourceRevision = AudioTestDatabase.Hash
        };
        db.VocabularyAudioBindings.Add(reference); await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        await AuthorizeAndUnbind(db, f.Clock, specific.Asset.Id);
        Assert.Equal(0, (await f.Service(db).RunBatchAsync(Token)).Observed); Assert.Equal(0, f.Store.Removes);
        var repo = new AudioInvalidationRepository(db, f.Clock); var waiting = (await repo.GetAsync(specific.Asset.Id, Token))!;
        Assert.Null(waiting.DeleteStartedAtUnixMs); Assert.Equal(0, waiting.Attempts); Assert.Equal("audio.invalidation_referenced", waiting.ErrorCode);
        await db.VocabularyAudioBindings.Where(row => row.Id == "reference").ExecuteDeleteAsync(Token);
        f.Clock.Now = waiting.NextAttemptAtUnixMs;
        Assert.Equal(1, (await f.Service(db).RunBatchAsync(Token)).Observed); Assert.Contains(historical.Asset.Location, f.Store.Objects);
        Assert.Null(await repo.GetAsync(historical.Asset.Id, Token));
    }

    [Fact]
    public async Task LaterInvalidationOfAnotherSpecificReference_ReusesAuthorityWithoutBlockingItsEdit()
    {
        using var f = new Fixture(); await using var db = f.File.Open(); await Init(db);
        var old = await Publish(db, f.Clock, "meaning");
        db.VocabularyAudioBindings.Add(new()
        {
            Id = "shared",
            WordId = "other",
            MeaningId = "other-meaning",
            Locale = AudioLocale.EnUs,
            AssetId = old.Asset.Id,
            SourceRevision = AudioTestDatabase.Hash
        });
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
        await AuthorizeAndUnbind(db, f.Clock, old.Asset.Id);
        var repo = new AudioInvalidationRepository(db, f.Clock);
        await new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            Assert.True(await repo.AuthorizeInTransactionAsync(new("other", "other-meaning", AudioLocale.EnUs, old.Asset.Id, 8), 10, Token));
            return await db.VocabularyAudioBindings.Where(row => row.Id == "shared").ExecuteDeleteAsync(Token);
        });
        Assert.Equal("meaning", (await repo.GetAsync(old.Asset.Id, Token))!.MeaningId);
        Assert.Equal(1, (await f.Service(db).RunBatchAsync(Token)).Observed);
    }

    [Fact]
    public async Task AuthorityRequiresExactLiveSpecificBindingAndSameWriterContext_AndRollsBackWithCaller()
    {
        using var f = new Fixture(); await using var db = f.File.Open(); await Init(db);
        var defaultAudio = await Publish(db, f.Clock, null); var specific = await Publish(db, f.Clock, "meaning");
        var repo = new AudioInvalidationRepository(db, f.Clock); var auth = new AudioInvalidationAuthorization("word", "meaning", AudioLocale.EnGb, specific.Asset.Id, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.AuthorizeInTransactionAsync(auth, 10, Token));
        await using var other = f.File.Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UnitOfWork(other).ExecuteInTransactionAsync(() => repo.AuthorizeInTransactionAsync(auth, 10, Token)));
        await new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            Assert.False(await repo.AuthorizeInTransactionAsync(auth with { AssetId = defaultAudio.Asset.Id }, 10, Token));
            Assert.False(await repo.AuthorizeInTransactionAsync(auth with { MeaningId = "other-meaning" }, 10, Token));
            Assert.False(await repo.AuthorizeInTransactionAsync(auth with { Locale = AudioLocale.EnUs }, 10, Token));
            return 0;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            Assert.True(await repo.AuthorizeInTransactionAsync(auth, 10, Token));
            await db.VocabularyAudioBindings.Where(row => row.AssetId == specific.Asset.Id).ExecuteDeleteAsync(Token);
            return await Task.FromException<int>(new InvalidOperationException("Injected caller failure."));
        }));
        Assert.Null(await repo.GetAsync(specific.Asset.Id, Token)); Assert.Equal(2, await db.VocabularyAudioBindings.CountAsync(Token));
        await AuthorizeAndUnbind(db, f.Clock, specific.Asset.Id);
        await new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            Assert.True(await repo.AuthorizeInTransactionAsync(auth, 10, Token));
            Assert.False(await repo.AuthorizeInTransactionAsync(auth with { Epoch = 2 }, 10, Token)); return 0;
        });
    }

    [Theory]
    [InlineData("lost-response")]
    [InlineData("missing")]
    [InlineData("still-present")]
    [InlineData("stat-failure")]
    [InlineData("delete-failure")]
    public async Task DeleteAndStatOutcomes_AreObservedOrPersistentlyRetried(string outcome)
    {
        using var f = new Fixture(); await using var db = f.File.Open(); await Init(db);
        var old = await Publish(db, f.Clock, "meaning"); await AuthorizeAndUnbind(db, f.Clock, old.Asset.Id);
        if (outcome != "missing") f.Store.Objects.Add(old.Asset.Location);
        f.Store.LoseResponse = outcome == "lost-response"; f.Store.Keep = outcome == "still-present";
        f.Store.StatFail = outcome == "stat-failure"; f.Store.RemoveFail = outcome == "delete-failure";
        var success = outcome is "lost-response" or "missing";
        for (var i = 1; i <= (success ? 1 : 7); i++)
        {
            var result = await f.Service(db).RunBatchAsync(Token);
            Assert.Equal(success ? 1 : 0, result.Observed); Assert.Equal(success ? 0 : 1, result.Failed);
            var row = (await new AudioInvalidationRepository(db, f.Clock).GetAsync(old.Asset.Id, Token))!;
            Assert.Equal(success ? AudioInvalidationState.Observed : AudioInvalidationState.Waiting, row.State);
            Assert.Equal(i, row.Attempts); Assert.Equal(success ? null : "audio.invalidation_failed", row.ErrorCode);
            Assert.NotNull(row.DeleteStartedAtUnixMs); f.Clock.Now = row.NextAttemptAtUnixMs;
        }
        if (!success)
        {
            f.Store.Keep = f.Store.StatFail = f.Store.RemoveFail = false;
            Assert.Equal(1, (await f.Service(db).RunBatchAsync(Token)).Observed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrTimeoutAndLeaseExpiry_ResumeAfterRestartAndRejectStaleOwner(bool timeout)
    {
        using var f = new Fixture(); AudioStoredUpload old; AudioInvalidation claimed;
        await using (var db = f.File.Open())
        {
            await Init(db); old = await Publish(db, f.Clock, "meaning"); await AuthorizeAndUnbind(db, f.Clock, old.Asset.Id);
            f.Store.Objects.Add(old.Asset.Location); f.Store.Block = true;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var running = f.Service(db).RunBatchAsync(stop.Token); await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
            if (!timeout) stop.Cancel();
            if (timeout) Assert.Equal(1, (await running.WaitAsync(TimeSpan.FromSeconds(3), Token)).Failed);
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(3), Token));
            claimed = (await new AudioInvalidationRepository(db, f.Clock).GetAsync(old.Asset.Id, Token))!;
            Assert.NotEqual(AudioInvalidationState.Observed, claimed.State);
            f.Store.Finish.TrySetResult(); f.Store.Block = false;
        }
        f.Clock.Now += 301000;
        await using (var restarted = f.File.Open())
        {
            var repo = new AudioInvalidationRepository(restarted, f.Clock);
            await f.Service(restarted).RunBatchAsync(Token);
            Assert.False(await repo.ObserveAsync(old.Asset.Id, claimed.Lease, f.Clock.Now, f.Clock.Now + 1000, Token));
            f.Clock.Now = (await repo.GetAsync(old.Asset.Id, Token))!.NextAttemptAtUnixMs;
            Assert.Equal(1, (await f.Service(restarted).RunBatchAsync(Token)).Observed);
            Assert.Equal(AudioInvalidationState.Observed, (await repo.GetAsync(old.Asset.Id, Token))!.State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferenceAndClaimCompeteOnTwoConnections_DatabaseTriggersPreventCheckThenDeleteRace(bool referenceFirst)
    {
        using var f = new Fixture(); await using var db = f.File.Open(); await Init(db);
        var old = await Publish(db, f.Clock, "meaning"); await AuthorizeAndUnbind(db, f.Clock, old.Asset.Id);
        await using var external = f.File.Open(); var repo = new AudioInvalidationRepository(db, f.Clock);
        async Task Reference() => await external.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO vocabulary_audio_binding VALUES ('reference','word',NULL,'en-GB',{old.Asset.Id},{AudioTestDatabase.Hash})", Token);
        if (referenceFirst)
        {
            await Reference(); Assert.Null(await repo.ClaimAsync(old.Asset.Id, "cleaner", 10, 1000, 100, Token));
            // Raw SQL cannot bypass the no-reference condition either.
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE audio_invalidation SET state='Claimed',lease_owner='bypass',lease_fence=1,lease_expires_at_unix_ms=1000,delete_started_at_unix_ms=10 WHERE asset_id={old.Asset.Id}", Token));
            await external.VocabularyAudioBindings.Where(row => row.Id == "reference").ExecuteDeleteAsync(Token);
            f.Clock.Now = 100;
        }
        var claim = (await repo.ClaimAsync(old.Asset.Id, "cleaner", f.Clock.Now, 1000, 200, Token))!;
        Assert.NotNull(claim);
        Assert.Equal(19, (await Assert.ThrowsAsync<SqliteException>(Reference)).SqliteErrorCode);
        var replacement = await Publish(external, f.Clock, null, AudioLocale.EnUs);
        await Assert.ThrowsAsync<SqliteException>(() => external.Database.ExecuteSqlInterpolatedAsync($"UPDATE vocabulary_audio_binding SET asset_id={old.Asset.Id} WHERE asset_id={replacement.Asset.Id}", Token));
        Assert.True(await repo.ReleaseAsync(old.Asset.Id, claim.Lease, f.Clock.Now, 500, Token));
        await Assert.ThrowsAsync<SqliteException>(Reference); // failed delete does not release the permanent tombstone
        foreach (var sql in new[] { "DELETE FROM audio_invalidation", "UPDATE audio_invalidation SET delete_started_at_unix_ms=NULL", "UPDATE audio_invalidation SET authorization_epoch=2", "UPDATE audio_asset SET object_key='changed' WHERE id='" + old.Asset.Id + "'" })
            await Assert.ThrowsAsync<SqliteException>(() => external.Database.ExecuteSqlRawAsync(sql, Token));
    }

    [Fact]
    public async Task ClaimChecksReferencesUnderActualSqliteWriterReservation()
    {
        using var f = new Fixture(); AudioStoredUpload old;
        await using (var seed = f.File.Open())
        { await Init(seed); old = await Publish(seed, f.Clock, "meaning"); await AuthorizeAndUnbind(seed, f.Clock, old.Asset.Id); }
        var race = new AttemptExternalReference(f.File, old.Asset.Id);
        await using var db = f.File.Open(race);
        var repo = new AudioInvalidationRepository(db, f.Clock);
        var claim = await repo.ClaimAsync(old.Asset.Id, "cleaner", 10, 1000, 100, Token);
        Assert.NotNull(claim); Assert.True(race.BlockedBySqlite);
        Assert.False(await repo.ObserveAsync(old.Asset.Id, claim.Lease with { Owner = "other" }, 10, 100, Token));
        Assert.False(await repo.ReleaseAsync(old.Asset.Id, claim.Lease with { Fence = claim.Lease.Fence + 1 }, 10, 100, Token));
    }

    [Fact]
    public async Task BlockedS3DoesNotHoldWriter_AndStorageDisabledOrUnsafePathsNeverContactS3()
    {
        using var f = new Fixture(); await using var db = f.File.Open(); await Init(db);
        var old = await Publish(db, f.Clock, "meaning"); await AuthorizeAndUnbind(db, f.Clock, old.Asset.Id);
        Assert.Equal(0, (await new AudioInvalidationRecoveryService(new AudioInvalidationRepository(db, f.Clock), f.Store, new(false, "test-audio", "audio/"), f.Clock).RunBatchAsync(Token)).Examined);
        f.Store.Block = true; var task = f.Service(db).RunBatchAsync(Token); await f.Store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        await using var other = f.File.Open();
        await new UnitOfWork(other).ExecuteInTransactionAsync(() => other.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET word='edited' WHERE id='other'", Token));
        Assert.Empty(await new AudioInvalidationRepository(other, f.Clock).FindRecoverableAsync(f.Clock.Now, 20, Token));
        f.Store.Finish.TrySetResult(); Assert.Equal(1, (await task).Observed);
        // A recorded authorization outside the currently configured destination is never sent to S3.
        f.Clock.Now += 86400000; var before = f.Store.Removes;
        Assert.Equal(1, (await new AudioInvalidationRecoveryService(new AudioInvalidationRepository(db, f.Clock), f.Store,
            new(true, "other-bucket", "audio/"), f.Clock).RunBatchAsync(Token)).Failed);
        Assert.Equal(before, f.Store.Removes);
    }

    [Theory]
    [InlineData("audio/../unsafe.mp3")]
    [InlineData("audio/UPPERCASE.mp3")]
    [InlineData("other/00000000000000000000000000000000.mp3")]
    public async Task AuthorizedButMalformedLocation_IsNeverRemoved(string key)
    {
        using var f = new Fixture(); await using var db = f.File.Open(); await Init(db);
        var old = await Publish(db, f.Clock, "meaning");
        await db.AudioAssets.Where(row => row.Id == old.Asset.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ObjectKey, key), Token);
        await AuthorizeAndUnbind(db, f.Clock, old.Asset.Id);
        Assert.Equal(1, (await f.Service(db).RunBatchAsync(Token)).Failed); Assert.Equal(0, f.Store.Removes);
    }

    private static async Task Init(VocabularyDbContext db) { await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db); }
    private static AudioUploadRequest Request(string key, string? meaning, AudioLocale locale = AudioLocale.EnGb) => new(key, AudioTestDatabase.Hash,
        "word", meaning, locale, AudioTestDatabase.Revision(locale, meaning != null), null, AudioTestDatabase.Hash, 128, "test-audio");
    private static async Task<AudioStoredUpload> Publish(VocabularyDbContext db, Clock clock, string? meaning, AudioLocale locale = AudioLocale.EnGb)
    {
        var repo = new AudioUploadRepository(db, clock); var upload = (await repo.ReserveAsync(Request(Guid.NewGuid().ToString(), meaning, locale), "uploader", 1, 300000, Token)).Upload!;
        Assert.True(await repo.BeginTransferAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        Assert.Equal(200, (await repo.CompleteAsync(upload.Operation.IdempotencyKey, upload.Operation.UploadLease, 3, Token))!.Receipt!.StatusCode);
        return upload;
    }
    private static Task<int> AuthorizeAndUnbind(VocabularyDbContext db, Clock clock, string assetId) => new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
    {
        Assert.True(await new AudioInvalidationRepository(db, clock).AuthorizeInTransactionAsync(new("word", "meaning", AudioLocale.EnGb, assetId, 1), 10, Token));
        return await db.VocabularyAudioBindings.Where(row => row.WordId == "word" && row.MeaningId == "meaning" && row.Locale == AudioLocale.EnGb && row.AssetId == assetId).ExecuteDeleteAsync(Token);
    });
    private sealed class AttemptExternalReference(AudioTestDatabase file, string assetId) : DbCommandInterceptor
    {
        public bool BlockedBySqlite;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!BlockedBySqlite && command.CommandText.Contains("FROM \"vocabulary_audio_binding\"", StringComparison.Ordinal))
            {
                await using var external = new SqliteConnection($"Data Source={file.PathName};Pooling=False;Default Timeout=1");
                await external.OpenAsync(cancellationToken); await using var insert = external.CreateCommand(); insert.CommandTimeout = 1;
                insert.CommandText = "INSERT INTO vocabulary_audio_binding VALUES ('racer','word',NULL,'en-GB',$asset,$revision)";
                insert.Parameters.AddWithValue("$asset", assetId); insert.Parameters.AddWithValue("$revision", AudioTestDatabase.Hash);
                try { await insert.ExecuteNonQueryAsync(cancellationToken); }
                catch (SqliteException error) when (error.SqliteErrorCode == 5) { BlockedBySqlite = true; }
            }
            return result;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly AudioTestDatabase File = new(); public readonly Clock Clock = new(); public readonly Store Store = new();
        public AudioInvalidationRecoveryService Service(VocabularyDbContext db) => new(new AudioInvalidationRepository(db, Clock), Store,
            new(true, "test-audio", "audio/", TimeoutSeconds: 1, MaxRetrySeconds: 120), Clock);
        public void Dispose() { Store.Finish.TrySetResult(); File.Dispose(); }
    }
    private sealed class Clock : TimeProvider { public long Now = 10; public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now); }
    private sealed class Store : IAudioObjectStore
    {
        public readonly HashSet<AudioObjectLocation> Objects = []; public int Removes; public bool LoseResponse, Keep, StatFail, RemoveFail, Block;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RemoveAsync(AudioObjectLocation location, CancellationToken token)
        {
            Entered.TrySetResult(); if (Block) await Finish.Task;
            Removes++; if (RemoveFail) throw new InvalidOperationException("synthetic-private-error");
            if (!Keep) Objects.Remove(location); if (LoseResponse) throw new IOException("synthetic-lost-response");
        }
        public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token) => StatFail ? throw new IOException("synthetic-stat-error")
            : Task.FromResult(Objects.Contains(location) ? new AudioObjectInfo(128, "audio/mpeg") : null);
        public Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType, CancellationToken token) => throw new NotSupportedException();
        public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => throw new NotSupportedException();
    }
}
