using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed partial class EnrichmentTaskRepository
{
    // Content validation still rejects bad results. These codes authorize one
    // fresh, bounded attempt sequence, not publication of the rejected content.
    private static readonly string[] RecoverableErrors = EnrichmentRecoveryPolicy.Errors;

    public async Task<int> RecoverAsync(int limit, IReadOnlyList<string> excludedWords,
        EnrichmentAdminConfiguration configuration, long now, CancellationToken token)
    {
        if (limit is < 1 or > 20 || excludedWords.Count > 4096) throw new ArgumentOutOfRangeException(nameof(limit));
        if (!configuration.CanPrepare) return 0;
        var candidates = await EnrichmentAdminQueries.Rows(context, configuration, now, true)
            .Where(r => r.AutomaticRecoveryAvailable && r.Step != "Fields" && r.ErrorCode != "preparation.failed" && !excludedWords.Contains(r.WordId))
            .OrderBy(r => r.UpdatedAtUnixMs).ThenBy(r => r.Id).Take(limit * 4)
            .Select(r => new { r.Id, r.WordId }).ToArrayAsync(token);
        var recovered = 0;
        var reserved = excludedWords.ToHashSet(StringComparer.Ordinal);
        foreach (var item in candidates)
        {
            if (recovered == limit) break;
            if (!reserved.Add(item.WordId)) continue;
            if (await _unitOfWork.ExecuteAdmittedTransactionAsync(async () =>
            {
                var target = await Targets.SingleAsync(t => t.Id == item.Id);
                var previous = await RoundQuery(target.Id, target.CurrentRound).AsNoTracking().SingleAsync();
                if (target.Deleted || previous.State != EnrichmentState.Failed || previous.RecoveryId != null
                    || !RecoverableErrors.Contains(previous.ErrorCode)) return false;
                if (await (from t in Targets
                           join r in Rounds on new { TargetId = t.Id, Number = t.CurrentRound }
                    equals new { r.TargetId, r.Number }
                           where t.WordId == target.WordId && r.State == EnrichmentState.Running
                    && r.LeaseExpiresAtUnixMs > now
                           select t.Id).AnyAsync()) return false;
                // Recheck only this word under the writer; never build the
                // entire catalog's demand set while blocking other writes.
                var demand = await EnrichmentAdminQueries.Rows(context, configuration, now, true, target.WordId).SingleAsync(r => r.Id == target.Id);
                if (!demand.AutomaticRecoveryAvailable) return false;
                var key = Key(target);
                if (key.Step == EnrichmentStep.Audio && (!configuration.AudioEnabled || !configuration.StorageEnabled
                    || !(key.Locale == AudioLocale.EnGb ? configuration.CanTtsUk : configuration.CanTtsUs))) return false;
                var input = await InputAsync(key, CancellationToken.None);
                if (input == null) return false;
                var version = configuration.Version(key);
                if (await context.EnrichmentRecoveries.AnyAsync(a => a.TargetId == target.Id && a.InputRevision == input.Revision
                    && a.ConfigurationVersion == version)) return false;
                var recovery = new EnrichmentRecoveryEntity
                {
                    Id = Guid.NewGuid().ToString(),
                    TargetId = target.Id,
                    InputRevision = input.Revision,
                    ConfigurationVersion = version,
                    SourceRound = previous.Number,
                    Reason = previous.ErrorCode!,
                    CreatedAtUnixMs = now
                };
                context.EnrichmentRecoveries.Add(recovery); await context.SaveChangesAsync(); context.ChangeTracker.Clear();
                async Task Renew(EnrichmentTargetKey renewKey)
                {
                    var task = await EnsureCoreAsync(renewKey, configuration.Version(renewKey), now,
                        Math.Min(5, configuration.FailureBudget), recovery.Id, true);
                    if (task != null) await RoundQuery(task.Id, task.CurrentRound).ExecuteUpdateAsync(set => set
                        .SetProperty(r => r.RecoveryId, recovery.Id).SetProperty(r => r.ForceSingle, true)
                        .SetProperty(r => r.NextAttemptAtUnixMs, now + 1000));
                }
                await Renew(key);
                if (key.Step == EnrichmentStep.Audio)
                    await Renew(key with { Step = EnrichmentStep.Prepare });
                if (key.Step == EnrichmentStep.Prepare)
                {
                    var dependents = await (from t in Targets
                                            join r in Rounds on new { TargetId = t.Id, Number = t.CurrentRound }
                        equals new { r.TargetId, r.Number }
                                            where !t.Deleted && t.WordId == key.WordId && t.MeaningId == key.MeaningId && t.Locale == key.Locale
                                                && t.Step != EnrichmentStep.Prepare && r.State == EnrichmentState.Failed
                            && (r.ErrorCode == "preparation.failed" || r.RequestCount == 0
                                && (r.ErrorCode == "tts.capability_unsupported" || r.ErrorCode == "tts.prepared_plan_required"))
                                            select t).ToArrayAsync();
                    foreach (var dependent in dependents) await Renew(Key(dependent));
                }
                return true;
            }, token)) recovered++;
        }
        return recovered;
    }
}
