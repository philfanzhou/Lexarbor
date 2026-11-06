using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed partial class EnrichmentTaskRepository
{
    private bool _batchWrite;
    // Only the batch repository may join these public per-target operations to its short writer.
    internal Task<T> BatchTransactionAsync<T>(Func<Task<T>> action, CancellationToken token) =>
        _unitOfWork.ExecuteAdmittedTransactionAsync(async () =>
        {
            if (_batchWrite || !UnitOfWork.OwnsWriteContext(context)) throw new InvalidOperationException("Invalid batch writer context.");
            _batchWrite = true;
            try { return await action(); }
            finally { _batchWrite = false; }
        }, token);

    internal async Task ReleaseBatchAsync(EnrichmentExecutionGuard guard, long now, CancellationToken token)
    {
        RequireTransaction(token);
        if (!_batchWrite || !await ValidAsync(guard, now) || await HasOpenCallAsync(guard)) return;
        await Owned(guard, Now(now)).ExecuteUpdateAsync(s => s.SetProperty(r => r.State, EnrichmentState.Pending)
            .SetProperty(r => r.LeaseOwner, (string?)null).SetProperty(r => r.LeaseExpiresAtUnixMs, (long?)null)
            .SetProperty(r => r.WaitReason, (EnrichmentWaitReason?)null).SetProperty(r => r.NextAttemptAtUnixMs, (long?)null)
            .SetProperty(r => r.UpdatedAtUnixMs, Now(now)), token);
    }
}
