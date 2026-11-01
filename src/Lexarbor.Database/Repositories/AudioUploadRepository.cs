using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class AudioUploadRepository(VocabularyDbContext context, TimeProvider? timeProvider = null)
    : IAudioUploadRepository, IAudioGuardedPublicationRepository
{
    private const long CleanupDelay = 24 * 60 * 60 * 1000L;
    private readonly UnitOfWork _unitOfWork = new(context);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<AudioStoredUpload?> GetAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        var row = await (from op in Operations
                         join asset in context.AudioAssets.AsNoTracking() on op.AssetId equals asset.Id
                         where op.IdempotencyKey == idempotencyKey
                         select new { op, asset }).SingleOrDefaultAsync(cancellationToken);
        return row == null ? null : new(Map(row.op), Map(row.asset));
    }

    public Task<AudioReservation> ReserveAsync(AudioUploadRequest request, string owner, long now, long leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        ValidateLease(owner, now, leaseExpiresAt);
        var segments = request.KeyPrefix.Split('/');
        if (segments.Length is < 2 or > 5 || segments[^1] != "" || segments[..^1].Any(segment => segment.Length == 0
            || segment.Any(ch => !(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
            throw new ArgumentException("Audio key prefix is invalid.", nameof(request));
        if (!Guid.TryParseExact(request.IdempotencyKey, "D", out var uuid) || uuid.ToString() != request.IdempotencyKey)
            throw new ArgumentException("Idempotency key must be a canonical UUID.", nameof(request));
        return WriteAsync<AudioReservation>(async () =>
        {
            ValidateLease(owner, now, leaseExpiresAt);
            var existing = await GetAsync(request.IdempotencyKey, CancellationToken.None);
            if (existing != null)
                return new(existing.Operation.RequestFingerprint != request.RequestFingerprint ? AudioReservationDisposition.Conflict
                    : existing.Operation.Receipt != null ? AudioReservationDisposition.Replay : AudioReservationDisposition.InProgress, existing);
            var target = await TargetAsync(request.WordId, request.MeaningId, request.Locale);
            if (target == null) return new(AudioReservationDisposition.NotFound, null);
            if (target.Value.Revision != request.SourceRevision || target.Value.AssetId != request.ExpectedAssetId)
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
            var op = new AudioUploadOperationEntity
            {
                IdempotencyKey = request.IdempotencyKey,
                RequestFingerprint = request.RequestFingerprint,
                WordId = request.WordId,
                MeaningId = request.MeaningId,
                Locale = request.Locale,
                SourceRevision = request.SourceRevision,
                ExpectedAssetId = request.ExpectedAssetId,
                MeaningPhoneticEpoch = target.Value.Epoch,
                AssetId = asset.Id,
                Producer = request.Producer,
                Voice = request.Voice,
                CreatedAtUnixMs = now,
                UpdatedAtUnixMs = now,
                State = AudioUploadState.Reserved,
                UploadLeaseOwner = owner,
                UploadLeaseFence = 1,
                UploadLeaseExpiresAtUnixMs = leaseExpiresAt
            };
            context.AudioAssets.Add(asset); context.AudioUploadOperations.Add(op);
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
        return WriteAsync(async () => await OwnedUpload(key, lease, now).Where(op => op.UploadLeaseExpiresAtUnixMs < leaseExpiresAt)
            .ExecuteUpdateAsync(set => set.SetProperty(op => op.UploadLeaseExpiresAtUnixMs, leaseExpiresAt).SetProperty(op => op.UpdatedAtUnixMs, now)) == 1, cancellationToken, () => now = EffectiveNow(now));
    }

    public Task<AudioUploadOperation?> CompleteAsync(string key, AudioLease lease, long now, CancellationToken cancellationToken) =>
        CompleteCoreAsync(key, lease, null, now, cancellationToken);

    public Task<AudioUploadOperation?> CompleteGuardedAsync(string key, AudioLease lease, EnrichmentExecutionGuard guard,
        long now, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(guard);
        return CompleteCoreAsync(key, lease, guard, now, token);
    }

    private Task<AudioUploadOperation?> CompleteCoreAsync(string key, AudioLease lease, EnrichmentExecutionGuard? guard,
        long now, CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        var op = await OwnedUpload(key, lease, now).SingleOrDefaultAsync(row => row.State == AudioUploadState.Transferring);
        if (op == null) return null;
        var target = await TargetAsync(op.WordId, op.MeaningId, op.Locale);
        var authorized = guard == null || await context.EnrichmentTargets.AsNoTracking().AnyAsync(row => row.Id == guard.TargetId
            && row.WordId == op.WordId && row.MeaningId == op.MeaningId && row.Locale == op.Locale && row.Step == EnrichmentStep.Audio)
            && await new EnrichmentTaskRepository(context, _clock).ValidateInTransactionAsync(guard, now, CancellationToken.None);
        if (target == null || target.Value.Revision != op.SourceRevision || target.Value.AssetId != op.ExpectedAssetId
            || target.Value.Epoch != op.MeaningPhoneticEpoch || !authorized)
        {
            await FailAsync(OwnedUpload(key, lease, now), now, target == null ? 404 : 409,
                target == null ? "audio.target_not_found" : "audio.target_conflict");
        }
        else
        {
            var asset = await context.AudioAssets.AsNoTracking().SingleAsync(row => row.Id == op.AssetId);
            if (asset.FirstPublishedAtUnixMs != null) return null;
            if (await context.AudioInvalidations.AnyAsync(row => row.AssetId == op.AssetId && row.DeleteStartedAtUnixMs != null))
            {
                await FailAsync(OwnedUpload(key, lease, now), now, 409, "audio.target_conflict");
                return Map(await Operations.SingleAsync(row => row.IdempotencyKey == key));
            }
            // The transaction owns SQLite's writer reservation. Every condition
            // is re-read here, not from tracked pre-Put target instances.
            var bindings = context.VocabularyAudioBindings.Where(row => row.WordId == op.WordId
                && row.MeaningId == op.MeaningId && row.Locale == op.Locale);
            if (op.ExpectedAssetId == null)
            {
                var binding = new VocabularyAudioBindingEntity
                {
                    Id = Guid.NewGuid().ToString(),
                    WordId = op.WordId,
                    MeaningId = op.MeaningId,
                    Locale = op.Locale,
                    AssetId = op.AssetId,
                    SourceRevision = op.SourceRevision
                };
                context.VocabularyAudioBindings.Add(binding);
                try { await context.SaveChangesAsync(); }
                finally { context.Entry(binding).State = EntityState.Detached; }
            }
            else if (await bindings.Where(row => row.AssetId == op.ExpectedAssetId).ExecuteUpdateAsync(set =>
                    set.SetProperty(row => row.AssetId, op.AssetId).SetProperty(row => row.SourceRevision, op.SourceRevision)) != 1)
                throw new InvalidOperationException("Audio binding changed inside the publication transaction.");
            await context.AudioAssets.Where(row => row.Id == op.AssetId && row.FirstPublishedAtUnixMs == null)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.FirstPublishedAtUnixMs, now));
            await OwnedUpload(key, lease, now).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, AudioUploadState.Succeeded)
                .SetProperty(row => row.UpdatedAtUnixMs, now).SetProperty(row => row.ReceiptStatusCode, 200)
                .SetProperty(row => row.ReceiptAssetId, op.AssetId).SetProperty(row => row.ReceiptSourceRevision, op.SourceRevision)
                .SetProperty(row => row.CompletedAtUnixMs, now).SetProperty(row => row.UploadLeaseOwner, (string?)null)
                .SetProperty(row => row.UploadLeaseExpiresAtUnixMs, (long?)null));
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

    public Task<AudioStoredUpload?> ClaimCleanupAsync(string key, string owner, long now, long leaseExpiresAt, CancellationToken cancellationToken)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return WriteAsync(async () => await ReturnCleanupAsync(OwnedCleanup(key, lease, now), now, nextAttemptAt, errorCode) == 1, cancellationToken, () => now = EffectiveNow(now));
    }

    public Task<bool> ObserveCleanupAsync(string key, AudioLease lease, long now, long nextAttemptAt, CancellationToken cancellationToken)
    {
        ValidateRetry(now, nextAttemptAt);
        return WriteAsync(async () => await OwnedCleanup(key, lease, now).ExecuteUpdateAsync(set =>
            set.SetProperty(op => op.State, AudioUploadState.CleanupObserved).SetProperty(op => op.CleanupLeaseOwner, (string?)null)
                .SetProperty(op => op.CleanupLeaseExpiresAtUnixMs, (long?)null).SetProperty(op => op.CleanupReturnState, (AudioUploadState?)null)
                .SetProperty(op => op.CleanupObservedAtUnixMs, now).SetProperty(op => op.CleanupNextAttemptAtUnixMs, nextAttemptAt)
                .SetProperty(op => op.CleanupLastErrorCode, (string?)null).SetProperty(op => op.UpdatedAtUnixMs, now)) == 1, cancellationToken, () => now = EffectiveNow(now));
    }

    public Task<bool> ExpireCleanupAsync(string key, long now, long nextAttemptAt, CancellationToken cancellationToken)
    {
        ValidateRetry(now, nextAttemptAt);
        return WriteAsync(async () => await ReturnCleanupAsync(Operations.Where(op => op.IdempotencyKey == key
            && op.State == AudioUploadState.CleanupClaimed && op.CleanupLeaseExpiresAtUnixMs <= now), now, nextAttemptAt, "audio.cleanup_expired") == 1, cancellationToken, () => now = EffectiveNow(now));
    }

    public async Task<IReadOnlyList<AudioStoredUpload>> FindRecoverableAsync(long now, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var rows = await Operations.Where(op =>
            ((op.State == AudioUploadState.Reserved || op.State == AudioUploadState.Transferring) && op.UploadLeaseExpiresAtUnixMs <= now)
            || (op.State == AudioUploadState.CleanupClaimed && op.CleanupLeaseExpiresAtUnixMs <= now)
            || ((op.State == AudioUploadState.Abandoned || op.State == AudioUploadState.CleanupObserved)
                && op.CreatedAtUnixMs <= now - CleanupDelay && (op.UploadLeaseExpiresAtUnixMs == null || op.UploadLeaseExpiresAtUnixMs <= now)
                && (op.CleanupNextAttemptAtUnixMs == null || op.CleanupNextAttemptAtUnixMs <= now)
                && context.AudioAssets.Any(asset => asset.Id == op.AssetId && asset.FirstPublishedAtUnixMs == null)))
            .OrderBy(op => op.CreatedAtUnixMs).ThenBy(op => op.IdempotencyKey).Take(limit).ToListAsync(cancellationToken);
        var result = new List<AudioStoredUpload>();
        foreach (var row in rows) result.Add(await StoredAsync(row, cancellationToken));
        return result;
    }

    public Task<AudioUnbindDisposition> UnbindAsync(string wordId, string? meaningId, AudioLocale locale, string expectedAssetId,
        CancellationToken cancellationToken) => WriteAsync(async () =>
    {
        var target = await TargetAsync(wordId, meaningId, locale);
        if (target == null || target.Value.AssetId == null) return AudioUnbindDisposition.NotFound;
        if (target.Value.AssetId != expectedAssetId) return AudioUnbindDisposition.Conflict;
        await context.VocabularyAudioBindings.Where(row => row.WordId == wordId && row.MeaningId == meaningId && row.Locale == locale
            && row.AssetId == expectedAssetId).ExecuteDeleteAsync();
        return AudioUnbindDisposition.Removed;
    }, cancellationToken);

    private IQueryable<AudioUploadOperationEntity> Operations => context.AudioUploadOperations.AsNoTracking();
    private IQueryable<AudioUploadOperationEntity> OwnedUpload(string key, AudioLease lease, long now) => Operations.Where(op =>
        op.IdempotencyKey == key && (op.State == AudioUploadState.Reserved || op.State == AudioUploadState.Transferring)
        && op.UploadLeaseOwner == lease.Owner && op.UploadLeaseFence == lease.Fence && op.UploadLeaseExpiresAtUnixMs > now && lease.Owner != null);
    private IQueryable<AudioUploadOperationEntity> OwnedCleanup(string key, AudioLease lease, long now) => Operations.Where(op =>
        op.IdempotencyKey == key && op.State == AudioUploadState.CleanupClaimed && op.CleanupLeaseOwner == lease.Owner
        && op.CleanupLeaseFence == lease.Fence && op.CleanupLeaseExpiresAtUnixMs > now && lease.Owner != null);
    private static Task<int> FailAsync(IQueryable<AudioUploadOperationEntity> rows, long now, int status, string error) => rows.ExecuteUpdateAsync(set =>
        set.SetProperty(op => op.State, AudioUploadState.Abandoned).SetProperty(op => op.UpdatedAtUnixMs, now)
            .SetProperty(op => op.ReceiptStatusCode, status).SetProperty(op => op.ReceiptErrorCode, error).SetProperty(op => op.CompletedAtUnixMs, now)
            .SetProperty(op => op.UploadLeaseOwner, (string?)null).SetProperty(op => op.UploadLeaseExpiresAtUnixMs, (long?)null));
    private static Task<int> ReturnCleanupAsync(IQueryable<AudioUploadOperationEntity> rows, long now, long next, string error) => rows.ExecuteUpdateAsync(set =>
        set.SetProperty(op => op.State, op => op.CleanupReturnState!.Value).SetProperty(op => op.CleanupLeaseOwner, (string?)null)
            .SetProperty(op => op.CleanupLeaseExpiresAtUnixMs, (long?)null).SetProperty(op => op.CleanupReturnState, (AudioUploadState?)null)
            .SetProperty(op => op.CleanupNextAttemptAtUnixMs, next).SetProperty(op => op.CleanupLastErrorCode, error).SetProperty(op => op.UpdatedAtUnixMs, now));

    private long EffectiveNow(long supplied) => Math.Max(supplied, _clock.GetUtcNow().ToUnixTimeMilliseconds());
    private Task<T> WriteAsync<T>(Func<Task<T>> action, CancellationToken token, Action? admitted = null)
    {
        if (UnitOfWork.IsWriteInProgress) throw new InvalidOperationException("Audio operations must own their short database transaction.");
        return _unitOfWork.ExecuteAdmittedTransactionAsync(() => { admitted?.Invoke(); return action(); }, token);
    }
    private async Task<(string Revision, string? AssetId, long Epoch)?> TargetAsync(string wordId, string? meaningId, AudioLocale locale)
    {
        var word = await context.Vocabularies.AsNoTracking().SingleOrDefaultAsync(row => row.Id == wordId);
        if (word == null) return null;
        AudioMeaningInput? meaning = null;
        if (meaningId != null)
        {
            var row = await context.VocabularyMeanings.AsNoTracking().SingleOrDefaultAsync(row => row.Id == meaningId && row.VocabularyId == wordId);
            if (row == null) return null;
            meaning = new(row.Id, row.PartOfSpeech, row.Meaning, row.PhoneticUk, row.PhoneticUs);
        }
        var revision = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new(word.Id, word.Word, word.PhoneticUk, word.PhoneticUs), meaning, locale));
        var own = await context.VocabularyAudioBindings.AsNoTracking().SingleOrDefaultAsync(row => row.WordId == wordId && row.MeaningId == meaningId && row.Locale == locale);
        var epoch = meaningId == null ? 0 : await context.MeaningPhoneticEpochs.AsNoTracking().Where(row => row.WordId == wordId
            && row.MeaningId == meaningId && row.Locale == locale).Select(row => (long?)row.Epoch).SingleOrDefaultAsync() ?? 0;
        return (revision, own?.AssetId, epoch);
    }
    private async Task<AudioStoredUpload> StoredAsync(AudioUploadOperationEntity op, CancellationToken token) => new(Map(op),
        Map(await context.AudioAssets.AsNoTracking().SingleAsync(row => row.Id == op.AssetId, token)));
    private static AudioAsset Map(AudioAssetEntity row) => new(row.Id, new(row.Bucket, row.ObjectKey), row.Sha256, row.ByteLength, row.ContentType, row.CreatedAtUnixMs, row.FirstPublishedAtUnixMs);
    private static AudioUploadOperation Map(AudioUploadOperationEntity row) => new(row.IdempotencyKey, row.RequestFingerprint,
        row.WordId, row.MeaningId, row.Locale, row.SourceRevision, row.ExpectedAssetId, row.AssetId, row.Producer, row.Voice,
        row.State, row.CreatedAtUnixMs, row.UpdatedAtUnixMs, new(row.UploadLeaseOwner, row.UploadLeaseFence, row.UploadLeaseExpiresAtUnixMs),
        row.ReceiptStatusCode == null ? null : new(row.ReceiptStatusCode.Value, row.ReceiptAssetId, row.ReceiptSourceRevision, row.ReceiptErrorCode, row.CompletedAtUnixMs!.Value),
        new(new(row.CleanupLeaseOwner, row.CleanupLeaseFence, row.CleanupLeaseExpiresAtUnixMs), row.CleanupReturnState,
            row.CleanupAttempts, row.CleanupNextAttemptAtUnixMs, row.CleanupLastErrorCode, row.CleanupObservedAtUnixMs));
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
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
    }
}
