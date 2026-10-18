using System.Security.Cryptography;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Audio;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

internal static class TextbookRecoveryProbe
{
    public static async Task VerifyAsync(AudioOptions options, byte[] bytes)
    {
        using var root = new ProbeDatabaseRoot(); var clock = new RecoveryProbeClock();
        var dbOptions = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={root.Path};Pooling=False").Options;
        using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
        TextbookAudioStoredUpload published, abandoned;
        var version = Guid.NewGuid().ToString();
        await using (var db = new VocabularyDbContext(dbOptions))
        {
            await db.Database.MigrateAsync();
            db.VocabularyBooks.Add(new() { Id = "book", BookName = "Textbook" });
            db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "book", Number = 1 });
            db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "book", UnitId = "unit", Title = "Track", Version = version });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var repo = new TextbookAudioUploadRepository(db, clock);
            published = (await repo.ReserveAsync(Request(version), "publisher", 0, 300000, default)).Upload!;
            await repo.BeginTransferAsync(published.Operation.IdempotencyKey, published.Operation.UploadLease, 0, default);
            await Put(published.Asset.Location);
            var success = (await repo.CompleteAsync(published.Operation.IdempotencyKey, published.Operation.UploadLease, 0, default))!;
            Require(success.Receipt?.StatusCode == 200, "Textbook internal publication must succeed before recovery.");
            abandoned = (await repo.ReserveAsync(Request(success.Receipt!.Version!), "crashed-textbook", 0, 300000, default)).Upload!;
            await repo.BeginTransferAsync(abandoned.Operation.IdempotencyKey, abandoned.Operation.UploadLease, 0, default);
            await Put(abandoned.Asset.Location);
        }
        var backupPath = root.Path + ".backup";
        await using (var source = new SqliteConnection($"Data Source={root.Path};Pooling=False"))
        await using (var destination = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
        { await source.OpenAsync(); await destination.OpenAsync(); source.BackupDatabase(destination); }
        clock.Now = 86400001;
        await using (var restarted = new VocabularyDbContext(dbOptions))
        {
            // Remove every directory row. Operations and assets still authorize recovery;
            // a published historical object still has permanent deletion protection.
            await restarted.TextbookAudioTracks.ExecuteDeleteAsync(); await restarted.VocabularyBookUnits.ExecuteDeleteAsync(); await restarted.VocabularyBooks.ExecuteDeleteAsync();
            var repo = new TextbookAudioUploadRepository(restarted, clock);
            Require(await new AudioRecordExistenceRepository(restarted).HasAnyAsync(default), "Textbook-only history must protect the storage destination.");
            var deniedOptions = JsonSerializer.Deserialize<AudioOptions>(JsonSerializer.Serialize(options))!;
            deniedOptions.AccessKey = "synthetic-denied-access"; deniedOptions.SecretKey = "synthetic-denied-secret";
            using var denied = new S3AudioObjectStore(Options.Create(deniedOptions), TimeProvider.System);
            var settings = new AudioRecoverySettings(true, options.Bucket, options.KeyPrefix, TimeoutSeconds: 3);
            Require((await new AudioRecoveryService(repo, denied, settings, clock).RunBatchAsync(default)).Failed == 1,
                "Real S3 textbook deletion denial must persist a retry.");
            var failed = (await repo.GetAsync(abandoned.Operation.IdempotencyKey, default))!;
            var receipt = failed.Operation.Receipt;
            Require(receipt?.StatusCode == 503 && failed.Operation.Cleanup.LastErrorCode == "audio.cleanup_failed",
                "Textbook recovery must retain a safe terminal receipt.");
            Require(await store.StatAsync(abandoned.Asset.Location, default) != null, "Denied removal must leave the real object intact.");
            var recovery = new AudioRecoveryService(repo, store, settings, clock);
            Require((await recovery.RunBatchAsync(default)).Examined == 0, "Textbook retry must honor persistent backoff.");
            clock.Now = failed.Operation.Cleanup.NextAttemptAtUnixMs!.Value;
            Require((await recovery.RunBatchAsync(default)).Observed == 1 && await store.StatAsync(abandoned.Asset.Location, default) == null,
                "Restarted textbook recovery must remove the known unpublished real object.");
            var observed = (await repo.GetAsync(abandoned.Operation.IdempotencyKey, default))!;
            await Put(abandoned.Asset.Location); // A genuinely late network writer at the retained key.
            Require((await recovery.RunBatchAsync(default)).Examined == 0, "A textbook late Put must wait for its durable recheck.");
            clock.Now = observed.Operation.Cleanup.NextAttemptAtUnixMs!.Value;
            Require((await recovery.RunBatchAsync(default)).Observed == 1 && await store.StatAsync(abandoned.Asset.Location, default) == null,
                "A textbook tombstone must reclaim a late real Put.");
            Require((await repo.GetAsync(abandoned.Operation.IdempotencyKey, default))!.Operation.Receipt == receipt,
                "Cleanup and late Put must never rewrite the textbook failure receipt.");
            Require(await repo.CompleteAsync(abandoned.Operation.IdempotencyKey, abandoned.Operation.UploadLease, clock.Now, default) == null,
                "An old upload fence cannot publish after cleanup.");
            Require(await store.StatAsync(published.Asset.Location, default) != null,
                "A textbook published object must survive loss of every catalog reference.");
        }
        await using (var backup = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={backupPath};Pooling=False").Options))
        {
            Require(await backup.TextbookAudioUploadOperations.CountAsync() == 2 && await backup.AudioAssets.CountAsync() == 2,
                "A consistent backup must retain textbook operations and shared assets together.");
            var track = await backup.TextbookAudioTracks.SingleAsync();
            Require(track.CurrentAssetId == published.Asset.Id, "The historical backup must retain the textbook current asset.");
            var link = await store.GetPlaybackLinkAsync(published.Asset.Location, TimeSpan.FromSeconds(60), default);
            using var http = new HttpClient();
            Require((await http.GetByteArrayAsync(link.Url)).SequenceEqual(bytes), "The restored textbook backup must still read its real published MP3.");
        }
        await store.RemoveAsync(published.Asset.Location, default); // Harness-owned teardown only.
        Console.WriteLine("Private S3 textbook restart/denied-remove/retry/late-Put/receipt/history/backup recovery passed.");

        TextbookAudioUploadRequest Request(string expected) => new(Guid.NewGuid().ToString(), new string('a', 64), "book", "unit", "track", expected,
            0.5, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, options.Bucket, options.KeyPrefix);
        async Task Put(AudioObjectLocation location)
        { using var content = new MemoryStream(bytes); await store.PutAsync(location, content, bytes.Length, "audio/mpeg", default); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new ProbeFailure(message); }
}
