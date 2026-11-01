using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class AudioInvalidationRepository(VocabularyDbContext context, TimeProvider? timeProvider = null)
    : IAudioInvalidationRepository
{
    private readonly UnitOfWork _unitOfWork = new(context);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private IQueryable<AudioInvalidationEntity> Rows => context.AudioInvalidations.AsNoTracking();

    public async Task<bool> AuthorizeInTransactionAsync(AudioInvalidationAuthorization authorization, long now, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!UnitOfWork.OwnsWriteContext(context) || context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Audio invalidation requires the caller's serialized writer transaction on the same context.");
        ArgumentException.ThrowIfNullOrWhiteSpace(authorization.WordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorization.MeaningId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorization.AssetId);
        _ = authorization.Locale.ToCode();
        if (authorization.Epoch <= 0) throw new ArgumentOutOfRangeException(nameof(authorization));
        now = Now(now);
        var existing = await Rows.SingleOrDefaultAsync(row => row.AssetId == authorization.AssetId, token);
        if (existing != null && existing.WordId == authorization.WordId && existing.MeaningId == authorization.MeaningId
            && existing.Locale == authorization.Locale && existing.AuthorizationEpoch == authorization.Epoch) return true;
        // Capture authority from the exact old specific binding BEFORE the caller
        // unbinds it. An arbitrary unreferenced/default asset cannot be authorized.
        var asset = await context.AudioAssets.AsNoTracking().SingleOrDefaultAsync(asset => asset.Id == authorization.AssetId
            && asset.FirstPublishedAtUnixMs != null && context.VocabularyAudioBindings.Any(binding =>
                binding.WordId == authorization.WordId && binding.MeaningId == authorization.MeaningId
                && binding.Locale == authorization.Locale && binding.AssetId == asset.Id), token);
        if (asset == null) return false;
        // A second specific target may share an already authorized asset. Its
        // later edit must not fail: the first exact authority remains sufficient
        // and immutable, while all remaining references still protect the file.
        if (existing != null) return existing.Bucket == asset.Bucket && existing.ObjectKey == asset.ObjectKey;
        var row = new AudioInvalidationEntity
        {
            AssetId = asset.Id,
            Bucket = asset.Bucket,
            ObjectKey = asset.ObjectKey,
            WordId = authorization.WordId,
            MeaningId = authorization.MeaningId,
            Locale = authorization.Locale,
            AuthorizationEpoch = authorization.Epoch,
            CreatedAtUnixMs = now,
            UpdatedAtUnixMs = now,
            NextAttemptAtUnixMs = now
        };
        context.AudioInvalidations.Add(row);
        try { await context.SaveChangesAsync(token); }
        finally { context.Entry(row).State = EntityState.Detached; }
        return true;
    }

    public async Task<AudioInvalidation?> GetAsync(string assetId, CancellationToken token)
    { var row = await Rows.SingleOrDefaultAsync(row => row.AssetId == assetId, token); return row == null ? null : Map(row); }

    public async Task<IReadOnlyList<AudioInvalidation>> FindRecoverableAsync(long now, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return (await Rows.Where(row => row.State == AudioInvalidationState.Claimed ? row.LeaseExpiresAtUnixMs <= now
                : row.NextAttemptAtUnixMs <= now).OrderBy(row => row.NextAttemptAtUnixMs).ThenBy(row => row.AssetId)
            .Take(limit).ToListAsync(token)).Select(Map).ToArray();
    }

    public Task<AudioInvalidation?> ClaimAsync(string assetId, string owner, long now, long expiresAt,
        long referenceRetryAt, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner); Retry(now, expiresAt); Retry(now, referenceRetryAt);
        return WriteAsync<AudioInvalidation?>(async () =>
        {
            now = Now(now); if (expiresAt <= now || referenceRetryAt <= now) return null;
            var due = Rows.Where(row => row.AssetId == assetId && row.State != AudioInvalidationState.Claimed && row.NextAttemptAtUnixMs <= now);
            if (await context.VocabularyAudioBindings.AnyAsync(row => row.AssetId == assetId))
            {
                await due.ExecuteUpdateAsync(set => set.SetProperty(row => row.NextAttemptAtUnixMs, referenceRetryAt)
                    .SetProperty(row => row.ErrorCode, "audio.invalidation_referenced").SetProperty(row => row.UpdatedAtUnixMs, now));
                return null;
            }
            if (await due.Where(row => context.AudioAssets.Any(asset => asset.Id == row.AssetId
                    && asset.Bucket == row.Bucket && asset.ObjectKey == row.ObjectKey && asset.FirstPublishedAtUnixMs != null))
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.State, AudioInvalidationState.Claimed)
                    .SetProperty(row => row.DeleteStartedAtUnixMs, row => row.DeleteStartedAtUnixMs ?? now)
                    .SetProperty(row => row.LeaseOwner, owner).SetProperty(row => row.LeaseFence, row => row.LeaseFence + 1)
                    .SetProperty(row => row.LeaseExpiresAtUnixMs, expiresAt).SetProperty(row => row.Attempts, row => row.Attempts + 1)
                    .SetProperty(row => row.UpdatedAtUnixMs, now)) != 1) return null;
            return await GetAsync(assetId, CancellationToken.None);
        }, token);
    }

    public Task<bool> ExpireAsync(string assetId, long now, long nextAttemptAt, CancellationToken token)
    {
        Retry(now, nextAttemptAt);
        return WriteAsync(async () => await ReturnAsync(Rows.Where(row => row.AssetId == assetId
            && row.State == AudioInvalidationState.Claimed && row.LeaseExpiresAtUnixMs <= Now(now)), Now(now), nextAttemptAt,
            "audio.invalidation_expired") == 1, token);
    }
    public Task<bool> ReleaseAsync(string assetId, AudioLease lease, long now, long nextAttemptAt, CancellationToken token)
    {
        Retry(now, nextAttemptAt);
        return WriteAsync(async () => await ReturnAsync(Owned(assetId, lease, Now(now)), Now(now), nextAttemptAt,
            "audio.invalidation_failed") == 1, token);
    }
    public Task<bool> ObserveAsync(string assetId, AudioLease lease, long now, long nextAttemptAt, CancellationToken token)
    {
        Retry(now, nextAttemptAt);
        return WriteAsync(async () => await Owned(assetId, lease, Now(now)).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.State, AudioInvalidationState.Observed).SetProperty(row => row.ObservedAtUnixMs, Now(now))
            .SetProperty(row => row.LeaseOwner, (string?)null).SetProperty(row => row.LeaseExpiresAtUnixMs, (long?)null)
            .SetProperty(row => row.NextAttemptAtUnixMs, nextAttemptAt).SetProperty(row => row.ErrorCode, (string?)null)
            .SetProperty(row => row.UpdatedAtUnixMs, Now(now))) == 1, token);
    }
    private static Task<int> ReturnAsync(IQueryable<AudioInvalidationEntity> rows, long now, long next, string code) => rows.ExecuteUpdateAsync(set => set
        .SetProperty(row => row.State, AudioInvalidationState.Waiting).SetProperty(row => row.LeaseOwner, (string?)null)
        .SetProperty(row => row.LeaseExpiresAtUnixMs, (long?)null).SetProperty(row => row.NextAttemptAtUnixMs, next)
        .SetProperty(row => row.ErrorCode, code).SetProperty(row => row.UpdatedAtUnixMs, now));
    private IQueryable<AudioInvalidationEntity> Owned(string id, AudioLease lease, long now) => Rows.Where(row => row.AssetId == id
        && row.State == AudioInvalidationState.Claimed && lease.Owner != null && row.LeaseOwner == lease.Owner
        && row.LeaseFence == lease.Fence && row.LeaseExpiresAtUnixMs > now);
    private Task<T> WriteAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        if (UnitOfWork.IsWriteInProgress || context.Database.CurrentTransaction != null)
            throw new InvalidOperationException("Audio invalidation recovery must own its short writer transaction.");
        return _unitOfWork.ExecuteAdmittedTransactionAsync(action, token);
    }
    private long Now(long supplied)
    { if (supplied < 0) throw new ArgumentOutOfRangeException(nameof(supplied)); return Math.Max(supplied, _clock.GetUtcNow().ToUnixTimeMilliseconds()); }
    private static void Retry(long now, long next)
    { if (now < 0 || next <= now) throw new ArgumentOutOfRangeException(nameof(next)); }
    private static AudioInvalidation Map(AudioInvalidationEntity row) => new(row.AssetId, new(row.Bucket, row.ObjectKey), row.WordId,
        row.MeaningId, row.Locale, row.AuthorizationEpoch, row.State, new(row.LeaseOwner, row.LeaseFence, row.LeaseExpiresAtUnixMs),
        row.Attempts, row.NextAttemptAtUnixMs, row.DeleteStartedAtUnixMs, row.ObservedAtUnixMs, row.ErrorCode);
}
