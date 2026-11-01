using System.Security.Cryptography;
using System.Text.Json;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed record AudioUploadSettings(bool Enabled, string Bucket, string KeyPrefix, int TimeoutSeconds);
public sealed record AudioUploadContent(string IdempotencyKey, string WordId, string? MeaningId, AudioLocale Locale,
    string SourceRevision, string? ExpectedAssetId, byte[] Bytes, string? Producer, string? Voice);
public sealed record AudioUploadResult(int StatusCode, string? AssetId = null, string? SourceRevision = null, string? ErrorCode = null);

/// <summary>Network IO runs between short repository transactions. Receipts never include object locations.</summary>
public sealed class AudioUploadService(IAudioUploadRepository repository, IAudioObjectStore store,
    AudioUploadSettings settings, TimeProvider clock)
{
    public async Task<AudioUploadResult> UploadAsync(AudioUploadContent content, CancellationToken cancellationToken,
        EnrichmentExecutionGuard? executionGuard = null)
    {
        if (executionGuard != null && repository is not IAudioGuardedPublicationRepository)
            throw new InvalidOperationException("Background audio requires guarded publication.");
        if (!settings.Enabled) return new(503, ErrorCode: "audio.disabled");
        cancellationToken.ThrowIfCancellationRequested();
        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Bytes));
        // Fixed, versioned array order preserves null/empty distinctions and includes every request condition.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            1, content.WordId, content.MeaningId, content.Locale.ToCode(), content.SourceRevision,
            content.ExpectedAssetId, hash, content.Bytes.LongLength, content.Producer, content.Voice
        })));
        var request = new AudioUploadRequest(content.IdempotencyKey, fingerprint, content.WordId, content.MeaningId,
            content.Locale, content.SourceRevision, content.ExpectedAssetId, hash, content.Bytes.LongLength,
            settings.Bucket, content.Producer, content.Voice, settings.KeyPrefix);
        var now = Now();
        var reservation = await repository.ReserveAsync(request, Guid.NewGuid().ToString(), now, now + 300_000, cancellationToken);
        if (reservation.Disposition == AudioReservationDisposition.Replay) return Receipt(reservation.Upload!.Operation);
        if (reservation.Disposition == AudioReservationDisposition.NotFound) return new(404, ErrorCode: "audio.target_not_found");
        if (reservation.Disposition != AudioReservationDisposition.Created) return new(409, ErrorCode: "audio.target_conflict");
        var upload = reservation.Upload!;
        try
        {
            if (!await repository.BeginTransferAsync(content.IdempotencyKey, upload.Operation.UploadLease, Now(), cancellationToken))
                return await ExpiredAsync(content.IdempotencyKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            using var stream = new MemoryStream(content.Bytes, writable: false);
            try
            {
                // A misbehaving provider cannot keep the request or its publication alive past this deadline.
                await store.PutAsync(upload.Asset.Location, stream, content.Bytes.LongLength, "audio/mpeg", timeout.Token)
                    .WaitAsync(timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                var abandoned = await repository.AbandonAsync(content.IdempotencyKey, upload.Operation.UploadLease, Now(), 503,
                    "audio.storage_unavailable", CancellationToken.None);
                return abandoned ? await ReadFailureAsync(content.IdempotencyKey) : await ExpiredAsync(content.IdempotencyKey);
            }
            // The repository observes cancellation before admission, then completes an admitted atomic commit.
            var completed = executionGuard == null
                ? await repository.CompleteAsync(content.IdempotencyKey, upload.Operation.UploadLease, Now(), cancellationToken)
                : await ((IAudioGuardedPublicationRepository)repository).CompleteGuardedAsync(content.IdempotencyKey,
                    upload.Operation.UploadLease, executionGuard, Now(), cancellationToken);
            return completed == null ? await ExpiredAsync(content.IdempotencyKey) : Receipt(completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await repository.AbandonAsync(content.IdempotencyKey, upload.Operation.UploadLease, Now(), 503,
                "audio.upload_cancelled", CancellationToken.None);
            throw;
        }
    }

    private long Now() => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private async Task<AudioUploadResult> ExpiredAsync(string key)
    {
        await repository.ExpireAsync(key, Now(), CancellationToken.None);
        return await ReadFailureAsync(key);
    }
    private async Task<AudioUploadResult> ReadFailureAsync(string key)
    {
        var stored = await repository.GetAsync(key, CancellationToken.None);
        return stored?.Operation.Receipt == null ? new(503, ErrorCode: "audio.upload_expired") : Receipt(stored.Operation);
    }
    private static AudioUploadResult Receipt(AudioUploadOperation op)
    {
        var receipt = op.Receipt!;
        return new(receipt.StatusCode, receipt.AssetId, receipt.SourceRevision, receipt.ErrorCode);
    }
}
