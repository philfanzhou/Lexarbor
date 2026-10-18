using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

// Only recovery facts cross this adapter. Target fields and permanent receipts
// retain their own vocabulary/textbook shapes and never enter the shared runner.
internal sealed record RecoveryUpload(string Key, AudioUploadState State, AudioCleanupProgress Cleanup, AudioAsset Asset);

internal abstract class RecoverySource
{
    public abstract Task<IReadOnlyList<RecoveryUpload>> FindAsync(long now, int limit, CancellationToken token);
    public abstract Task<bool> ExpireAsync(string key, long now, CancellationToken token);
    public abstract Task<RecoveryUpload?> ClaimAsync(string key, string owner, long now, long expiry, CancellationToken token);
    public abstract Task<bool> ReleaseAsync(string key, AudioLease lease, long now, long next, string error, CancellationToken token);
    public abstract Task<bool> ObserveAsync(string key, AudioLease lease, long now, long next, CancellationToken token);
    public abstract Task<bool> ExpireCleanupAsync(string key, long now, long next, CancellationToken token);
}

internal sealed class VocabularyRecoverySource(IAudioUploadRepository repository) : RecoverySource
{
    private static RecoveryUpload Map(AudioStoredUpload row) => new(row.Operation.IdempotencyKey, row.Operation.State, row.Operation.Cleanup, row.Asset);
    public override async Task<IReadOnlyList<RecoveryUpload>> FindAsync(long now, int limit, CancellationToken token)
        => (await repository.FindRecoverableAsync(now, limit, token)).Select(Map).ToArray();
    public override Task<bool> ExpireAsync(string key, long now, CancellationToken token) => repository.ExpireAsync(key, now, token);
    public override async Task<RecoveryUpload?> ClaimAsync(string key, string owner, long now, long expiry, CancellationToken token)
        => await repository.ClaimCleanupAsync(key, owner, now, expiry, token) is { } row ? Map(row) : null;
    public override Task<bool> ReleaseAsync(string key, AudioLease lease, long now, long next, string error, CancellationToken token)
        => repository.ReleaseCleanupAsync(key, lease, now, next, error, token);
    public override Task<bool> ObserveAsync(string key, AudioLease lease, long now, long next, CancellationToken token)
        => repository.ObserveCleanupAsync(key, lease, now, next, token);
    public override Task<bool> ExpireCleanupAsync(string key, long now, long next, CancellationToken token)
        => repository.ExpireCleanupAsync(key, now, next, token);
}

internal sealed class TextbookRecoverySource(ITextbookAudioUploadRepository repository) : RecoverySource
{
    private static RecoveryUpload Map(TextbookAudioStoredUpload row) => new(row.Operation.IdempotencyKey, row.Operation.State, row.Operation.Cleanup, row.Asset);
    public override async Task<IReadOnlyList<RecoveryUpload>> FindAsync(long now, int limit, CancellationToken token)
        => (await repository.FindRecoverableAsync(now, limit, token)).Select(Map).ToArray();
    public override Task<bool> ExpireAsync(string key, long now, CancellationToken token) => repository.ExpireAsync(key, now, token);
    public override async Task<RecoveryUpload?> ClaimAsync(string key, string owner, long now, long expiry, CancellationToken token)
        => await repository.ClaimCleanupAsync(key, owner, now, expiry, token) is { } row ? Map(row) : null;
    public override Task<bool> ReleaseAsync(string key, AudioLease lease, long now, long next, string error, CancellationToken token)
        => repository.ReleaseCleanupAsync(key, lease, now, next, error, token);
    public override Task<bool> ObserveAsync(string key, AudioLease lease, long now, long next, CancellationToken token)
        => repository.ObserveCleanupAsync(key, lease, now, next, token);
    public override Task<bool> ExpireCleanupAsync(string key, long now, long next, CancellationToken token)
        => repository.ExpireCleanupAsync(key, now, next, token);
}
