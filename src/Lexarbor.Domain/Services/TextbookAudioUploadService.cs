using System.Security.Cryptography;
using System.Text.Json;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed record TextbookAudioUploadContent(string IdempotencyKey, string BookId, string UnitId, string TrackId,
    string ExpectedVersion, byte[] Bytes, string Sha256, double DurationSeconds);
public sealed record TextbookAudioUploadResult(int StatusCode, string? TrackId = null, string? Version = null,
    double? DurationSeconds = null, string? ErrorCode = null);

/// <summary>Media has already been validated. Only short repository calls surround storage IO.</summary>
public sealed class TextbookAudioUploadService(ITextbookAudioUploadRepository repository, IAudioObjectStore store,
    AudioUploadSettings settings, TimeProvider clock)
{
    public Task<TextbookAudioStoredUpload?> FindAsync(string key, CancellationToken token) => repository.GetAsync(key, token);

    public static string Fingerprint(string book, string unit, string track, string version, string hash, long bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object[]
            { "textbook-audio-upload", 1, book, unit, track, version, hash, bytes })));

    public static TextbookAudioUploadResult Match(TextbookAudioStoredUpload stored, string fingerprint) =>
        stored.Operation.RequestFingerprint != fingerprint || stored.Operation.Receipt == null
            ? new(409, ErrorCode: "audio.target_conflict") : Receipt(stored.Operation);

    public async Task<TextbookAudioUploadResult> UploadAsync(TextbookAudioUploadContent content, CancellationToken token,
        Func<IDisposable>? retainTransfer = null)
    {
        if (!settings.Enabled) return new(503, ErrorCode: "audio.disabled");
        token.ThrowIfCancellationRequested();
        var fingerprint = Fingerprint(content.BookId, content.UnitId, content.TrackId, content.ExpectedVersion, content.Sha256, content.Bytes.LongLength);
        var now = Now();
        var reservation = await repository.ReserveAsync(new(content.IdempotencyKey, fingerprint, content.BookId, content.UnitId,
            content.TrackId, content.ExpectedVersion, content.DurationSeconds, content.Sha256, content.Bytes.LongLength,
            settings.Bucket, settings.KeyPrefix), Guid.NewGuid().ToString(), now, now + 300_000, token);
        if (reservation.Disposition == AudioReservationDisposition.Replay) return Receipt(reservation.Upload!.Operation);
        if (reservation.Disposition == AudioReservationDisposition.NotFound) return new(404);
        if (reservation.Disposition != AudioReservationDisposition.Created) return new(409);
        var upload = reservation.Upload!;
        try
        {
            if (!await repository.BeginTransferAsync(content.IdempotencyKey, upload.Operation.UploadLease, Now(), token))
                return await ExpiredAsync(content.IdempotencyKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            try
            {
                // Transfer owns the stream and retained capacity until the actual provider finishes.
                await TransferAsync(upload.Asset.Location, content.Bytes, timeout.Token, retainTransfer).WaitAsync(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                await repository.AbandonAsync(content.IdempotencyKey, upload.Operation.UploadLease, Now(), 503,
                    "audio.storage_unavailable", CancellationToken.None);
                return await ReadAsync(content.IdempotencyKey);
            }
            var completed = await repository.CompleteAsync(content.IdempotencyKey, upload.Operation.UploadLease, Now(), token);
            return completed == null ? await ExpiredAsync(content.IdempotencyKey) : Receipt(completed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await repository.AbandonAsync(content.IdempotencyKey, upload.Operation.UploadLease, Now(), 503,
                "audio.upload_cancelled", CancellationToken.None);
            throw;
        }
        // A failed/unknown commit keeps its durable lease for recovery and original-key lookup.
    }
    private async Task TransferAsync(AudioObjectLocation location, byte[] bytes, CancellationToken token, Func<IDisposable>? retainTransfer)
    {
        using var transfer = retainTransfer?.Invoke();
        using var stream = new MemoryStream(bytes, writable: false);
        await store.PutAsync(location, stream, bytes.LongLength, "audio/mpeg", token);
    }
    private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private async Task<TextbookAudioUploadResult> ExpiredAsync(string key)
    {
        await repository.ExpireAsync(key, Now(), CancellationToken.None);
        return await ReadAsync(key);
    }
    private async Task<TextbookAudioUploadResult> ReadAsync(string key)
    {
        var stored = await repository.GetAsync(key, CancellationToken.None);
        return stored?.Operation.Receipt is null ? new(503) : Receipt(stored.Operation);
    }
    private static TextbookAudioUploadResult Receipt(TextbookAudioUploadOperation operation)
    {
        var receipt = operation.Receipt!;
        return new(receipt.StatusCode, receipt.TrackId, receipt.Version, receipt.DurationSeconds, receipt.ErrorCode);
    }
}
