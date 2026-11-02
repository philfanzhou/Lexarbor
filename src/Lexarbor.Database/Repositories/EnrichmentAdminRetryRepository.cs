using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed partial class EnrichmentTaskRepository
{
    public Task<EnrichmentRetryResult> RetryAsync(string id, int expectedRound, string retryKey,
        EnrichmentAdminConfiguration configuration, long now, CancellationToken token)
    {
        Uuid(retryKey);
        if (expectedRound is < 1 or int.MaxValue) throw new ArgumentOutOfRangeException(nameof(expectedRound));
        if (UnitOfWork.IsWriteInProgress || context.Database.CurrentTransaction != null)
            throw new InvalidOperationException("Admin retry requires its own serialized writer transaction.");
        return _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            now = Now(now);
            var target = await Targets.SingleOrDefaultAsync(t => t.Id == id, token);
            if (target == null) return new EnrichmentRetryResult(EnrichmentRetryStatus.NotFound, id);
            // Replays return the original accepted round, including after a restart
            // or after subsequent configuration changes have created more history.
            var replay = await Rounds.SingleOrDefaultAsync(r => r.TargetId == id && r.RetryKey == retryKey, token);
            if (replay != null) return new(replay.Number == expectedRound + 1 ? EnrichmentRetryStatus.Replayed : EnrichmentRetryStatus.Conflict,
                id, replay.Number);
            var current = await RoundQuery(id, target.CurrentRound).SingleAsync(token);
            var demand = await EnrichmentAdminQueries.Rows(context, configuration, now, tasks: true).SingleAsync(r => r.Id == id, token);
            if (target.Deleted || target.CurrentRound != expectedRound || current.State != EnrichmentState.Failed
                || !demand.Applicable || !demand.Missing) return new(EnrichmentRetryStatus.Conflict, id);
            var key = Key(target);
            ValidateOptions(key, configuration.Version(key), configuration.FailureBudget, retryKey);
            var renewed = await EnsureCoreAsync(key, configuration.Version(key), now, configuration.FailureBudget, retryKey, true);
            if (renewed == null || renewed.CurrentRound != expectedRound + 1)
                throw new InvalidOperationException("The retry round could not be created.");
            if (key.Step == EnrichmentStep.Fields)
            {
                // Fields failures can be the RESULT of a successful preparation
                // with missing/equivalent candidates. Never reuse that old plan.
                var prepare = key with { Step = EnrichmentStep.Prepare };
                await EnsureCoreAsync(prepare, configuration.Version(prepare), now, configuration.FailureBudget, null, true);
            }
            if (key.Step is EnrichmentStep.Prepare or EnrichmentStep.Fields)
            {
                var dependencies = await (from t in Targets
                                          join r in Rounds on new { TargetId = t.Id, Number = t.CurrentRound }
                    equals new { r.TargetId, r.Number }
                                          where !t.Deleted && t.Id != id && t.WordId == key.WordId && t.MeaningId == key.MeaningId && t.Locale == key.Locale
                                              && t.Step != EnrichmentStep.Prepare && r.State == EnrichmentState.Failed && r.ErrorCode == "preparation.failed"
                                          select t).ToArrayAsync(token);
                foreach (var dependent in dependencies)
                {
                    var status = await EnrichmentAdminQueries.Rows(context, configuration, now, true).SingleAsync(r => r.Id == dependent.Id, token);
                    if (!status.Applicable || !status.Missing) continue;
                    var dependentKey = Key(dependent);
                    await EnsureCoreAsync(dependentKey, configuration.Version(dependentKey), now, configuration.FailureBudget, null, true);
                }
            }
            token.ThrowIfCancellationRequested();
            return new EnrichmentRetryResult(EnrichmentRetryStatus.Started, id, renewed.CurrentRound);
        }, token);
    }
}
