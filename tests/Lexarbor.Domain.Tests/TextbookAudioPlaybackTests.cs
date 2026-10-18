using System.Data;
using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class TextbookAudioPlaybackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task Seed(VocabularyDbContext db)
    {
        await db.Database.MigrateAsync(Ct); await AudioTestDatabase.SeedAsync(db);
        db.VocabularyBooks.Add(new() { Id = "other-book", BookName = "Other" });
        db.VocabularyBookUnits.Add(new() { Id = "other-unit", BookId = "book", Number = 2 });
        db.AudioAssets.AddRange(AudioTestDatabase.Asset("old"), AudioTestDatabase.Asset("new"), AudioTestDatabase.Asset("unpublished", false));
        db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "book", UnitId = "unit", Title = "Recording", CurrentAssetId = "old", DurationSeconds = 1, Version = Guid.NewGuid().ToString("D") });
        await db.SaveChangesAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=0 WHERE id='book'", Ct);
    }
    private static Task<TextbookAudioPlaybackSnapshot> Read(VocabularyDbContext db, string book = "book", string unit = "unit", string track = "track")
        => new TextbookAudioPlaybackReadRepository(db).GetAdminSnapshotAsync(book, unit, track, Ct);

    [Fact]
    public async Task Snapshot_UsesCurrentUntrackedPublishedFile_IgnoresAvailabilityAndBookStatus()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Seed(db);
        Assert.Equal("old", (await Read(db)).Asset.Id);
        // Tracked old state must not leak into subsequent snapshots.
        await db.Database.ExecuteSqlRawAsync("UPDATE textbook_audio_track SET current_asset_id='new'", Ct);
        Assert.Equal("new", (await Read(db)).Asset.Id);
        foreach (var sql in new[] { "UPDATE textbook_audio_track SET current_asset_id='unpublished'", "UPDATE textbook_audio_track SET current_asset_id=NULL,duration_seconds=NULL" })
        {
            await db.Database.ExecuteSqlRawAsync(sql, Ct);
            await Assert.ThrowsAsync<ResourceNotFoundException>(() => Read(db));
        }
        Assert.Null(db.Database.CurrentTransaction); Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData("missing", "unit", "track", false)]
    [InlineData("book", "missing", "track", false)]
    [InlineData("other-book", "unit", "track", true)]
    [InlineData("book", "unit", "missing", false)]
    [InlineData("book", "other-unit", "track", true)]
    public async Task Ownership_ChecksInCatalogueOrder(string book, string unit, string track, bool conflict)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await Seed(db);
        if (conflict) await Assert.ThrowsAsync<ConflictException>(() => Read(db, book, unit, track));
        else await Assert.ThrowsAsync<ResourceNotFoundException>(() => Read(db, book, unit, track));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("open", 5)]
    [InlineData("read", 5)]
    [InlineData("open", 6)]
    [InlineData("read", 6)]
    public async Task BusyOrLocked_MapsOpenAndReadFailures(string stage, int code)
    {
        using var file = new AudioTestDatabase(); await using (var setup = file.Open()) await Seed(setup);
        await using var db = file.Open(new BusyConnection(stage, code), new BusyRead(stage, code));
        var error = await Assert.ThrowsAsync<StorageBusyException>(() => Read(db));
        Assert.Equal("The vocabulary database is busy. Please retry the request.", error.Message);
    }

    [Fact]
    public async Task Snapshot_KeepsAssetWithSelectedTrackAcrossConcurrentReplacement()
    {
        using var file = new AudioTestDatabase(); await using var setup = file.Open(); await Seed(setup);
        await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = file.Open(new PauseAsset(entered, release));
        var pending = Read(reader); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            await setup.Database.ExecuteSqlRawAsync("UPDATE textbook_audio_track SET current_asset_id='new'", Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("old", (await pending).Asset.Id); Assert.Equal("new", (await Read(setup)).Asset.Id);
    }

    [Theory]
    [InlineData("selected", "disable", true)]
    [InlineData("stat", "disable", true)]
    [InlineData("sign", "disable", true)]
    [InlineData("selected", "down", true)]
    [InlineData("stat", "down", true)]
    [InlineData("sign", "down", true)]
    [InlineData("selected", "replace", false)]
    [InlineData("selected", "replace", true)]
    [InlineData("stat", "replace", false)]
    [InlineData("stat", "replace", true)]
    [InlineData("sign", "replace", false)]
    [InlineData("sign", "replace", true)]
    [InlineData("selected", "track", false)]
    [InlineData("selected", "track", true)]
    [InlineData("stat", "track", false)]
    [InlineData("stat", "track", true)]
    [InlineData("sign", "track", false)]
    [InlineData("sign", "track", true)]
    [InlineData("selected", "parents", false)]
    [InlineData("selected", "parents", true)]
    [InlineData("stat", "parents", false)]
    [InlineData("stat", "parents", true)]
    [InlineData("sign", "parents", false)]
    [InlineData("sign", "parents", true)]
    public async Task CompletedRead_AllowsWritesAndSignsOnlyCapturedAsset(string boundary, string mutation, bool publicTrack)
    {
        using var file = new AudioTestDatabase(); await using var reader = file.Open(); await Seed(reader);
        await reader.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=1; UPDATE textbook_audio_track SET is_available=1", Ct);
        await reader.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", Ct);
        async Task Mutate()
        {
            Assert.Null(reader.Database.CurrentTransaction); Assert.Equal(ConnectionState.Closed, reader.Database.GetDbConnection().State);
            await using var writer = file.Open();
            var sql = mutation switch
            {
                "replace" => "UPDATE textbook_audio_track SET current_asset_id='new'",
                "disable" => "UPDATE vocabulary_book SET status=0",
                "down" => "UPDATE textbook_audio_track SET is_available=0",
                _ => "DELETE FROM textbook_audio_track"
            };
            if (mutation == "parents") sql += "; DELETE FROM vocabulary_meaning_unit; DELETE FROM vocabulary_meaning; DELETE FROM vocabulary_book_unit; DELETE FROM vocabulary_book";
            await writer.Database.ExecuteSqlRawAsync(sql, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        var repository = new AfterSnapshot(new TextbookAudioPlaybackReadRepository(reader), boundary == "selected" ? Mutate : null);
        var store = new Store(boundary == "stat" ? Mutate : null, boundary == "sign" ? Mutate : null);
        var service = new TextbookAudioPlaybackService(repository, store, new(120, 3));
        var result = publicTrack ? await service.GetPublicAsync("book", "unit", "track", Ct) : await service.GetAdminAsync("book", "unit", "track", Ct);
        Assert.Equal("track", result.TrackId); Assert.Equal(TimeSpan.Zero, result.Link.ExpiresAt.Offset);
        Assert.Equal(new[] { "audio/old.mp3" }, store.Stats); Assert.Equal(store.Stats, store.Signs);
        Assert.Equal(TimeSpan.FromSeconds(120), store.Validity);
    }


    [Theory]
    [InlineData("disable")]
    [InlineData("down")]
    [InlineData("replace")]
    [InlineData("delete")]
    public async Task PublicSnapshot_BookAndCurrentShareWalSnapshot(string mutation)
    {
        using var file = new AudioTestDatabase(); await using var setup = file.Open(); await Seed(setup);
        await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; UPDATE vocabulary_book SET status=1; UPDATE textbook_audio_track SET is_available=1", Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = file.Open(new PausePublicTrack(entered, release));
        var pending = new TextbookAudioPlaybackReadRepository(reader).GetPublicSnapshotAsync("book", "unit", "track", Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            var sql = mutation switch
            {
                "disable" => "UPDATE vocabulary_book SET status=0",
                "down" => "UPDATE textbook_audio_track SET is_available=0",
                "replace" => "UPDATE textbook_audio_track SET current_asset_id='new'",
                _ => "DELETE FROM textbook_audio_track"
            };
            await setup.Database.ExecuteSqlRawAsync(sql, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("old", (await pending).Asset.Id);
        Assert.Null(reader.Database.CurrentTransaction); Assert.Equal(ConnectionState.Closed, reader.Database.GetDbConnection().State);
        var next = new TextbookAudioPlaybackReadRepository(setup);
        if (mutation == "disable") await Assert.ThrowsAsync<BusinessRuleException>(() => next.GetPublicSnapshotAsync("book", "unit", "track", Ct));
        else if (mutation != "replace") await Assert.ThrowsAsync<ResourceNotFoundException>(() => next.GetPublicSnapshotAsync("book", "unit", "track", Ct));
        else Assert.Equal("new", (await next.GetPublicSnapshotAsync("book", "unit", "track", Ct)).Asset.Id);
    }

    [Fact]
    public async Task PublicList_CountPageShareWalSnapshot_AndNeverReuseTrackedState()
    {
        using var file = new AudioTestDatabase(); await using var setup = file.Open(); await Seed(setup);
        await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; UPDATE vocabulary_book SET status=1; UPDATE textbook_audio_track SET is_available=1", Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = file.Open(new PausePublicPage(entered, release));
        var pending = new TextbookAudioPlaybackReadRepository(reader).GetPublicPageAsync("book", "unit", 1, 20, Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try { await setup.Database.ExecuteSqlRawAsync("UPDATE textbook_audio_track SET is_available=0", Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct); }
        finally { release.TrySetResult(); }
        var captured = await pending; Assert.Equal(1, captured.TotalCount); Assert.Single(captured.Items);
        var next = await new TextbookAudioPlaybackReadRepository(setup).GetPublicPageAsync("book", "unit", 1, 20, Ct);
        Assert.Equal(0, next.TotalCount); Assert.Empty(next.Items);
        await setup.Database.ExecuteSqlRawAsync("UPDATE textbook_audio_track SET is_available=1,current_asset_id='new'", Ct);
        Assert.Equal("new", (await new TextbookAudioPlaybackReadRepository(setup).GetPublicSnapshotAsync("book", "unit", "track", Ct)).Asset.Id);
    }

    [Theory]
    [InlineData("open", 5, false)]
    [InlineData("read", 5, false)]
    [InlineData("open", 6, false)]
    [InlineData("read", 6, false)]
    [InlineData("open", 5, true)]
    [InlineData("read", 5, true)]
    [InlineData("open", 6, true)]
    [InlineData("read", 6, true)]
    public async Task PublicReads_MapBusyWithoutWriting(string stage, int code, bool list)
    {
        using var file = new AudioTestDatabase(); await using (var setup = file.Open()) await Seed(setup);
        await using var db = file.Open(new BusyConnection(stage, code), new BusyRead(stage, code));
        var repository = new TextbookAudioPlaybackReadRepository(db);
        await Assert.ThrowsAsync<StorageBusyException>(async () =>
        {
            if (list) await repository.GetPublicPageAsync("book", "unit", 1, 20, Ct);
            else await repository.GetPublicSnapshotAsync("book", "unit", "track", Ct);
        });
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicReadCancellation_ReleasesSnapshotWithoutStorage(bool list)
    {
        using var file = new AudioTestDatabase(); await using var setup = file.Open(); await Seed(setup);
        await setup.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=1; UPDATE textbook_audio_track SET is_available=1", Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reader = file.Open(new PausePublicTrack(entered, release));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var store = new Store(null, null);
        var service = new TextbookAudioPlaybackService(new TextbookAudioPlaybackReadRepository(reader), store, new(120, 3));
        async Task Read()
        {
            if (list) await service.ListPublicAsync("book", "unit", 1, 20, cancellation.Token);
            else await service.GetPublicAsync("book", "unit", "track", cancellation.Token);
        }
        var pending = Read(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(reader.Database.CurrentTransaction); Assert.Equal(ConnectionState.Closed, reader.Database.GetDbConnection().State);
        Assert.Empty(store.Stats); Assert.Empty(store.Signs);
    }

    private sealed class PausePublicTrack(TaskCompletionSource entered, TaskCompletionSource release) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (command.CommandText.Contains("FROM \"textbook_audio_track\"", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); } return result; }
    }
    private sealed class PausePublicPage(TaskCompletionSource entered, TaskCompletionSource release) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (command.CommandText.Contains("FROM \"textbook_audio_track\"", StringComparison.Ordinal) && command.CommandText.Contains("LIMIT", StringComparison.Ordinal)) { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); } return result; }
    }
    private sealed class AfterSnapshot(ITextbookAudioPlaybackReadRepository inner, Func<Task>? after) : ITextbookAudioPlaybackReadRepository
    {
        public Task<TextbookAudioTrackPage> GetPublicPageAsync(string book, string unit, int page, int size, CancellationToken ct)
            => inner.GetPublicPageAsync(book, unit, page, size, ct);
        public async Task<TextbookAudioPlaybackSnapshot> GetPublicSnapshotAsync(string book, string unit, string track, CancellationToken ct)
        { var result = await inner.GetPublicSnapshotAsync(book, unit, track, ct); if (after != null) await after(); return result; }
        public async Task<TextbookAudioPlaybackSnapshot> GetAdminSnapshotAsync(string book, string unit, string track, CancellationToken ct)
        { var result = await inner.GetAdminSnapshotAsync(book, unit, track, ct); if (after != null) await after(); return result; }
    }
    private sealed class Store(Func<Task>? stat, Func<Task>? sign) : IAudioObjectStore
    {
        public readonly List<string> Stats = []; public readonly List<string> Signs = []; public TimeSpan Validity;
        public async Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken ct)
        { Stats.Add(location.Key); if (stat != null) await stat(); return new(128, "audio/mpeg"); }
        public async Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken ct)
        { Signs.Add(location.Key); Validity = validity; if (sign != null) await sign(); return new("https://example.test/signed", DateTimeOffset.Now.Add(validity)); }
        public Task PutAsync(AudioObjectLocation l, Stream s, long length, string type, CancellationToken ct)
            => PutAsync(l, s, length, type, AudioUploadPurpose.Vocabulary, ct);
        public Task PutAsync(AudioObjectLocation l, Stream s, long length, string type, AudioUploadPurpose purpose, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(AudioObjectLocation l, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class PauseAsset(TaskCompletionSource entered, TaskCompletionSource release) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (command.CommandText.Contains("FROM \"audio_asset\"", StringComparison.Ordinal)) { entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); } return result; }
    }
    private sealed class BusyConnection(string stage, int code) : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData data, InterceptionResult result, CancellationToken ct = default)
            => stage == "open" ? throw new SqliteException("synthetic-busy", code) : ValueTask.FromResult(result);
    }
    private sealed class BusyRead(string stage, int code) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
            => stage == "read" ? throw new SqliteException("synthetic-busy", code) : ValueTask.FromResult(result);
    }
}
