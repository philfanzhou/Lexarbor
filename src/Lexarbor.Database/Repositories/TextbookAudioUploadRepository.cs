using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class TextbookAudioUploadRepository(VocabularyDbContext context, TimeProvider? timeProvider = null) : ITextbookAudioUploadRepository
{
    private const long CleanupDelay = 24 * 60 * 60 * 1000L;
    private readonly UnitOfWork _unitOfWork = new(context);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<TextbookAudioStoredUpload?> GetAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            var row = await (from op in Operations
                             join asset in context.AudioAssets.AsNoTracking() on op.AssetId equals asset.Id
                             where op.IdempotencyKey == idempotencyKey
                             select new { op, asset }).SingleOrDefaultAsync(cancellationToken);
            return row == null ? null : new(Map(row.op), Map(row.asset));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new StorageBusyException("The vocabulary database is busy. Please retry the request.", exception);
        }
    }

    public Task<TextbookAudioReservation> ReserveAsync(TextbookAudioUploadRequest request, string owner, long now, long leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        ValidateLease(owner, now, leaseExpiresAt);
        var segments = request.KeyPrefix.Split('/');
        if (segments.Length is < 2 or > 5 || segments[^1] != "" || segments[..^1].Any(segment => segment.Length is < 1 or > 32 || !(segment[0] is >= 'a' and <= 'z' or >= '0' and <= '9')
            || segment.Any(ch => !(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
            throw new ArgumentException("Audio key prefix is invalid.", nameof(request));
        if (!Guid.TryParseExact(request.IdempotencyKey, "D", out var uuid) || uuid.ToString() != request.IdempotencyKey)
            throw new ArgumentException("Idempotency key must be a canonical UUID.", nameof(request));
        return WriteAsync<TextbookAudioReservation>(async () =>
        {
            ValidateLease(owner, now, leaseExpiresAt);
            var existing = await GetAsync(request.IdempotencyKey, CancellationToken.None);
            if (existing != null)
                return new(existing.Operation.RequestFingerprint != request.RequestFingerprint ? AudioReservationDisposition.Conflict
                    : existing.Operation.Receipt != null ? AudioReservationDisposition.Replay : AudioReservationDisposition.InProgress, existing);
            var (disposition, target) = await TargetAsync(request.BookId, request.UnitId, request.TrackId);
            if (target == null) return new(disposition, null);
            if (target.Version != request.ExpectedVersion)
                return new(AudioReservationDisposition.Conflict, null);
            var asset = new AudioAssetEntity
            {
                Id = Guid.NewGuid().ToString(),
                Bucket = request.Bucket,
                ObjectKey = $"{request.KeyPrefix}{Guid.NewGuid():N}.mp3",
                Sha256 = request.Sha256,
                ByteLength = request.ByteLength,
                ContentType = "audio/mpeg",
                CreatedAtUnixMs = now
            };
            var op = new TextbookAudioUploadOperationEntity
            {
                IdempotencyKey = request.IdempotencyKey,
                RequestFingerprint = request.RequestFingerprint,
                BookId = request.BookId,
                UnitId = request.UnitId,
                TrackId = request.TrackId,
                ExpectedVersion = request.ExpectedVersion,
                DurationSeconds = request.DurationSeconds,
                AssetId = asset.Id,
                CreatedAtUnixMs = now,
                UpdatedAtUnixMs = now,
                State = AudioUploadState.Reserved,
                UploadLeaseOwner = owner,
                UploadLeaseFence = 1,
                UploadLeaseExpiresAtUnixMs = leaseExpiresAt
            };
            context.AudioAssets.Add(asset); context.TextbookAudioUploadOperations.Add(op);
            try { await context.SaveChangesAsync(); }
            finally { context.Entry(asset).State = EntityState.Detached; context.Entry(op).State = EntityState.Detached; }
            return new(AudioReservationDisposition.Created, new(Map(op), Map(asset)));
        }, cancellationToken, () => now = EffectiveNow(now));
    }

    public Task<bool> BeginTransferAsync(string key, AudioLease lease, long now, CancellationToken cancellationToken) => WriteAsync(async () =>
        await OwnedUpload(key, lease, now).Where(op => op.State == AudioUploadState.Reserved)
            .ExecuteUpdateAsync(set => set.SetProperty(op => op.State, AudioUploadState.Transferring).SetProperty(op => op.UpdatedAtUnixMs, now)) == 1, cancellationToken, () => now = EffectiveNow(now));

    public Task<bool> RenewAsync(string key, AudioLease lease, long now, long leaseExpiresAt, CancellationToken cancellationToken)
    {
        if (now < 0 || leaseExpiresAt <= now) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        return WriteAsync(async () => leaseExpiresAt > now && await OwnedUpload(key, lease, now).Where(op => op.UploadLeaseExpiresAtUnixMs < leaseExpiresAt)
            .ExecuteUpdateAsync(set => set.SetProperty(op => op.UploadLeaseExpiresAtUnixMs, leaseExpiresAt).SetProperty(op => op.UpdatedAtUnixMs, now)) == 1, cancellationToken, () => now = EffectiveNow(now));
    }

    public Task<TextbookAudioUploadOperation?> CompleteAsync(string key, AudioLease lease, long now, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        var op = await OwnedUpload(key, lease, now).SingleOrDefaultAsync(row => row.State == AudioUploadState.Transferring);
        if (op == null) return null;
        var (disposition, target) = await TargetAsync(op.BookId, op.UnitId, op.TrackId);
        if (target == null || target.Version != op.ExpectedVersion)
        {
            var missing = target == null && disposition == AudioReservationDisposition.NotFound;
            await FailAsync(OwnedUpload(key, lease, now), now, missing ? 404 : 409,
                missing ? "audio.target_not_found" : "audio.target_conflict");
        }
        else
        {
            var asset = await context.AudioAssets.AsNoTracking().SingleAsync(row => row.Id == op.AssetId);
            if (asset.FirstPublishedAtUnixMs != null) return null;
            var version = Guid.NewGuid().ToString();
            // All target and lease reads happen after SQLite writer admission.
            // A failed receipt write/commit rolls back the directory and publication together.
            if (await context.TextbookAudioTracks.Where(row => row.Id == op.TrackId && row.BookId == op.BookId
                    && row.UnitId == op.UnitId && row.Version == op.ExpectedVersion)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.CurrentAssetId, op.AssetId)
                    .SetProperty(row => row.DurationSeconds, op.DurationSeconds).SetProperty(row => row.IsAvailable, false)
                    .SetProperty(row => row.Version, version)) != 1)
                throw new InvalidOperationException("Textbook audio track changed inside the publication transaction.");
            if (await context.AudioAssets.Where(row => row.Id == op.AssetId && row.FirstPublishedAtUnixMs == null)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.FirstPublishedAtUnixMs, now)) != 1)
                throw new InvalidOperationException("Textbook audio asset changed inside the publication transaction.");
            if (await OwnedUpload(key, lease, now).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, AudioUploadState.Succeeded)
                .SetProperty(row => row.UpdatedAtUnixMs, now).SetProperty(row => row.ReceiptStatusCode, 200)
                .SetProperty(row => row.ReceiptAssetId, op.AssetId).SetProperty(row => row.ReceiptTrackId, op.TrackId)
                .SetProperty(row => row.ReceiptVersion, version).SetProperty(row => row.ReceiptDurationSeconds, op.DurationSeconds)
                .SetProperty(row => row.CompletedAtUnixMs, now).SetProperty(row => row.UploadLeaseOwner, (string?)null)
                .SetProperty(row => row.UploadLeaseExpiresAtUnixMs, (long?)null)) != 1)
                throw new InvalidOperationException("Textbook audio lease changed inside the publication transaction.");
        }
        return Map(await Operations.SingleAsync(row => row.IdempotencyKey == key));
    }, cancellationToken, () => now = EffectiveNow(now));

    public Task<bool> AbandonAsync(string key, AudioLease lease, long now, int statusCode, string errorCode, CancellationToken cancellationToken)
    {
        ValidateFailure(statusCode, errorCode);
        return WriteAsync(async () => await FailAsync(OwnedUpload(key, lease, now), now, statusCode, errorCode) == 1, cancellationToken, () => now = EffectiveNow(now));
    }

    public Task<bool> ExpireAsync(string key, long now, CancellationToken cancellationToken) => WriteAsync(async () =>
        await FailAsync(Operations.Where(op => op.IdempotencyKey == key && (op.State == AudioUploadState.Reserved || op.State == AudioUploadState.Transferring)
            && op.UploadLeaseExpiresAtUnixMs <= now), now, 503, "audio.upload_expired") == 1, cancellationToken, () => now = EffectiveNow(now));

    public Task<TextbookAudioStoredUpload?> ClaimCleanupAsync(string key, string owner, long now, long leaseExpiresAt, CancellationToken cancellationToken)
    {
        ValidateLease(owner, now, leaseExpiresAt);
        return WriteAsync(async () =>
        {
            if (leaseExpiresAt <= now) return null;
            var eligible = Operations.Where(op => op.IdempotencyKey == key
                && (op.State == AudioUploadState.Abandoned || op.State == AudioUploadState.CleanupObserved)
                && op.CreatedAtUnixMs <= now - CleanupDelay
                && (op.UploadLeaseExpiresAtUnixMs == null || op.UploadLeaseExpiresAtUnixMs <= now)
                && (op.CleanupNextAttemptAtUnixMs == null || op.CleanupNextAttemptAtUnixMs <= now)
                && context.AudioAssets.Any(asset => asset.Id == op.AssetId && asset.FirstPublishedAtUnixMs == null));
            if (await eligible.ExecuteUpdateAsync(set => set.SetProperty(op => op.CleanupReturnState, op => op.State)
                .SetProperty(op => op.State, AudioUploadState.CleanupClaimed).SetProperty(op => op.CleanupLeaseOwner, owner)
                .SetProperty(op => op.CleanupLeaseFence, op => op.CleanupLeaseFence + 1)
                .SetProperty(op => op.CleanupLeaseExpiresAtUnixMs, leaseExpiresAt).SetProperty(op => op.UpdatedAtUnixMs, now)
                .SetProperty(op => op.CleanupAttempts, op => op.CleanupAttempts + 1)) != 1) return null;
            return await GetAsync(key, CancellationToken.None);
        }, cancellationToken, () => now = EffectiveNow(now));
    }

    public Task<bool> ReleaseCleanupAsync(string key, AudioLease lease, long now, long nextAttemptAt, string errorCode, CancellationToken cancellationToken)
    {
        ValidateRetry(now, nextAttemptAt);
        if (errorCode is not ("audio.cleanup_failed" or "audio.cleanup_expired"))
            throw new ArgumentException("Audio cleanup code is invalid.", nameof(errorCode));
        return WriteAsync(async () => nextAttemptAt > now && await ReturnCleanupAsync(OwnedCleanup(key, lease, now), now, nextAttemptAt, errorCode) == 1, cancellationToken, () => now = EffectiveNow(now));
    }

    public Task<bool> ObserveCleanupAsync(string key, AudioLease lease, long now, long nextAttemptAt, CancellationToken cancellationToken)
    {
        ValidateRetry(now, nextAttemptAt);
        return WriteAsync(async () => nextAttemptAt > now && await OwnedCleanup(key, lease, now).ExecuteUpdateAsync(set =>
            set.SetProperty(op => op.State, AudioUploadState.CleanupObserved).SetProperty(op => op.CleanupLeaseOwner, (string?)null)
                .SetProperty(op => op.CleanupLeaseExpiresAtUnixMs, (long?)null).SetProperty(op => op.CleanupReturnState, (AudioUploadState?)null)
                .SetProperty(op => op.CleanupObservedAtUnixMs, now).SetProperty(op => op.CleanupNextAttemptAtUnixMs, nextAttemptAt)
                .SetProperty(op => op.CleanupLastErrorCode, (string?)null).SetProperty(op => op.UpdatedAtUnixMs, now)) == 1, cancellationToken, () => now = EffectiveNow(now));
    }

    public Task<bool> ExpireCleanupAsync(string key, long now, long nextAttemptAt, CancellationToken cancellationToken)
    {
        ValidateRetry(now, nextAttemptAt);
        return WriteAsync(async () => nextAttemptAt > now && await ReturnCleanupAsync(Operations.Where(op => op.IdempotencyKey == key
            && op.State == AudioUploadState.CleanupClaimed && op.CleanupLeaseExpiresAtUnixMs <= now), now, nextAttemptAt, "audio.cleanup_expired") == 1, cancellationToken, () => now = EffectiveNow(now));
    }

    public async Task<IReadOnlyList<TextbookAudioStoredUpload>> FindRecoverableAsync(long now, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        try
        {
            var rows = await (from op in Operations
                              join asset in context.AudioAssets.AsNoTracking() on op.AssetId equals asset.Id
                              where ((op.State == AudioUploadState.Reserved || op.State == AudioUploadState.Transferring) && op.UploadLeaseExpiresAtUnixMs <= now)
                                  || (op.State == AudioUploadState.CleanupClaimed && op.CleanupLeaseExpiresAtUnixMs <= now)
                                  || ((op.State == AudioUploadState.Abandoned || op.State == AudioUploadState.CleanupObserved)
                                      && op.CreatedAtUnixMs <= now - CleanupDelay
                                      && (op.UploadLeaseExpiresAtUnixMs == null || op.UploadLeaseExpiresAtUnixMs <= now)
                                      && (op.CleanupNextAttemptAtUnixMs == null || op.CleanupNextAttemptAtUnixMs <= now)
                                      && asset.FirstPublishedAtUnixMs == null)
                              orderby op.CreatedAtUnixMs, op.IdempotencyKey
                              select new { op, asset }).Take(limit).ToListAsync(cancellationToken);
            return rows.Select(row => new TextbookAudioStoredUpload(Map(row.op), Map(row.asset))).ToArray();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new StorageBusyException("The vocabulary database is busy. Please retry the request.", exception);
        }
    }

    private IQueryable<TextbookAudioUploadOperationEntity> Operations => context.TextbookAudioUploadOperations.AsNoTracking();
    private IQueryable<TextbookAudioUploadOperationEntity> OwnedUpload(string key, AudioLease lease, long now) => Operations.Where(op =>
        op.IdempotencyKey == key && (op.State == AudioUploadState.Reserved || op.State == AudioUploadState.Transferring)
        && op.UploadLeaseOwner == lease.Owner && op.UploadLeaseFence == lease.Fence && op.UploadLeaseExpiresAtUnixMs > now && lease.Owner != null);
    private IQueryable<TextbookAudioUploadOperationEntity> OwnedCleanup(string key, AudioLease lease, long now) => Operations.Where(op =>
        op.IdempotencyKey == key && op.State == AudioUploadState.CleanupClaimed && op.CleanupLeaseOwner == lease.Owner
        && op.CleanupLeaseFence == lease.Fence && op.CleanupLeaseExpiresAtUnixMs > now && lease.Owner != null);
    private static Task<int> FailAsync(IQueryable<TextbookAudioUploadOperationEntity> rows, long now, int status, string error) => rows.ExecuteUpdateAsync(set =>
        set.SetProperty(op => op.State, AudioUploadState.Abandoned).SetProperty(op => op.UpdatedAtUnixMs, now)
            .SetProperty(op => op.ReceiptStatusCode, status).SetProperty(op => op.ReceiptErrorCode, error).SetProperty(op => op.CompletedAtUnixMs, now)
            .SetProperty(op => op.UploadLeaseOwner, (string?)null).SetProperty(op => op.UploadLeaseExpiresAtUnixMs, (long?)null));
    private static Task<int> ReturnCleanupAsync(IQueryable<TextbookAudioUploadOperationEntity> rows, long now, long next, string error) => rows.ExecuteUpdateAsync(set =>
        set.SetProperty(op => op.State, op => op.CleanupReturnState!.Value).SetProperty(op => op.CleanupLeaseOwner, (string?)null)
            .SetProperty(op => op.CleanupLeaseExpiresAtUnixMs, (long?)null).SetProperty(op => op.CleanupReturnState, (AudioUploadState?)null)
            .SetProperty(op => op.CleanupNextAttemptAtUnixMs, next).SetProperty(op => op.CleanupLastErrorCode, error).SetProperty(op => op.UpdatedAtUnixMs, now));

    private long EffectiveNow(long supplied) => Math.Max(supplied, _clock.GetUtcNow().ToUnixTimeMilliseconds());
    private Task<T> WriteAsync<T>(Func<Task<T>> action, CancellationToken token, Action? admitted = null)
    {
        if (UnitOfWork.IsWriteInProgress || context.Database.CurrentTransaction != null) throw new InvalidOperationException("Audio operations must own their short database transaction.");
        return _unitOfWork.ExecuteAdmittedTransactionAsync(() => { admitted?.Invoke(); return action(); }, token);
    }
    private async Task<(AudioReservationDisposition Disposition, TextbookAudioTrackEntity? Track)> TargetAsync(string bookId, string unitId, string trackId)
    {
        if (!await context.VocabularyBooks.AsNoTracking().AnyAsync(row => row.Id == bookId))
            return (AudioReservationDisposition.NotFound, null);
        var unit = await context.VocabularyBookUnits.AsNoTracking().SingleOrDefaultAsync(row => row.Id == unitId);
        if (unit == null) return (AudioReservationDisposition.NotFound, null);
        if (unit.BookId != bookId) return (AudioReservationDisposition.Conflict, null);
        var track = await context.TextbookAudioTracks.AsNoTracking().SingleOrDefaultAsync(row => row.Id == trackId);
        if (track == null) return (AudioReservationDisposition.NotFound, null);
        if (track.UnitId != unitId || track.BookId != bookId) return (AudioReservationDisposition.Conflict, null);
        return (AudioReservationDisposition.Created, track);
    }
    private static AudioAsset Map(AudioAssetEntity row) => new(row.Id, new(row.Bucket, row.ObjectKey), row.Sha256, row.ByteLength, row.ContentType, row.CreatedAtUnixMs, row.FirstPublishedAtUnixMs);
    private static TextbookAudioUploadOperation Map(TextbookAudioUploadOperationEntity row) => new(row.IdempotencyKey, row.RequestFingerprint,
        row.BookId, row.UnitId, row.TrackId, row.ExpectedVersion, row.DurationSeconds, row.AssetId,
        row.State, row.CreatedAtUnixMs, row.UpdatedAtUnixMs, new(row.UploadLeaseOwner, row.UploadLeaseFence, row.UploadLeaseExpiresAtUnixMs),
        row.ReceiptStatusCode == null ? null : new(row.ReceiptStatusCode.Value, row.ReceiptTrackId, row.ReceiptVersion, row.ReceiptDurationSeconds, row.ReceiptAssetId, row.ReceiptErrorCode, row.CompletedAtUnixMs!.Value),
        new(new(row.CleanupLeaseOwner, row.CleanupLeaseFence, row.CleanupLeaseExpiresAtUnixMs), row.CleanupReturnState,
            row.CleanupAttempts, row.CleanupNextAttemptAtUnixMs, row.CleanupLastErrorCode, row.CleanupObservedAtUnixMs));
    private static void ValidateRequest(TextbookAudioUploadRequest request)
    {
        if (!Guid.TryParseExact(request.ExpectedVersion, "D", out var version) || version.ToString() != request.ExpectedVersion)
            throw new ArgumentException("Expected version must be a canonical UUID.", nameof(request));
        foreach (var id in new[] { request.BookId, request.UnitId, request.TrackId })
            if (string.IsNullOrWhiteSpace(id) || id != id.Trim()) throw new ArgumentException("Target ID must be trimmed and nonempty.", nameof(request));
        foreach (var hash in new[] { request.RequestFingerprint, request.Sha256 })
            if (hash == null || hash.Length != 64 || hash.Any(ch => !(ch is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new ArgumentException("Audio hash must be lowercase SHA256.", nameof(request));
        if (!double.IsFinite(request.DurationSeconds) || request.DurationSeconds <= 0 || request.ByteLength <= 0)
            throw new ArgumentException("Audio duration and size must be positive and finite.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Bucket);
    }
    private static void ValidateLease(string? owner, long now, long expires)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (now < 0 || expires <= now) throw new ArgumentOutOfRangeException(nameof(expires));
    }
    private static void ValidateRetry(long now, long next)
    {
        if (now < 0 || next <= now) throw new ArgumentOutOfRangeException(nameof(next));
    }
    private static void ValidateFailure(int status, string error)
    {
        if (status is not (404 or 409 or 503)) throw new ArgumentOutOfRangeException(nameof(status));
        if (error is not ("audio.upload_failed" or "audio.upload_expired" or "audio.target_not_found" or "audio.target_conflict"
            or "audio.storage_unavailable" or "audio.upload_cancelled" or "audio.upload_timeout"))
            throw new ArgumentException("Audio failure code is invalid.", nameof(error));
    }
}
