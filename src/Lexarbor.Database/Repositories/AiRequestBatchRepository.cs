using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class AiRequestBatchRepository(VocabularyDbContext db, EnrichmentTaskRepository tasks,
    IMeaningSubmissionAutoReviewRepository reviews, IEnrichmentSchedulerRepository scheduler) : IAiRequestBatchRepository
{
    public async Task<int> LimitAsync(string kind, string configurationVersion, int initial, int maximum, CancellationToken token)
    {
        if (initial < 1 || maximum < initial || maximum > 10) throw new ArgumentOutOfRangeException(nameof(initial));
        if (kind == "Expand") return Math.Clamp(await db.AiRequestBatchPolicies.AsNoTracking()
            .Where(p => p.Kind == kind && p.ConfigurationVersion == configurationVersion).Select(p => (int?)p.Size)
            .SingleOrDefaultAsync(token) ?? 1, 1, Math.Min(2, maximum));
        var size = await db.AiRequestBatches.AsNoTracking().Where(r => r.Kind == kind && r.ConfigurationVersion == configurationVersion
            && r.FinishedAtUnixMs != null).OrderByDescending(r => r.FinishedAtUnixMs).ThenByDescending(r => r.StartedAtUnixMs)
            .Select(r => (int?)r.NextSize).FirstOrDefaultAsync(token);
        return Math.Clamp(size ?? initial, 1, maximum);
    }

    public Task<AiBatchReceipt?> BeginAsync(string kind, string configurationVersion, IReadOnlyList<AiBatchMember> members,
        long now, CancellationToken token) => tasks.BatchTransactionAsync<AiBatchReceipt?>(async () =>
    {
        if (kind is not ("Prepare" or "Expand" or "Review") || members.Count is < 1 or > 10
            || members.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != members.Count
            || configurationVersion.Length is < 1 or > 128) throw new ArgumentException("Invalid AI request batch.");
        var batch = new AiRequestBatchEntity { Id = Guid.NewGuid().ToString(), Kind = kind, ConfigurationVersion = configurationVersion, StartedAtUnixMs = now };
        var accepted = new List<AiBatchMember>();
        foreach (var member in members)
        {
            if (await db.AiRequestBatchMembers.AnyAsync(r => r.Id == member.Id, token)) continue;
            var valid = kind == "Review" ? member.Review != null && member.Guard == null && await reviews.IsCurrentAsync(member.Review, token)
                : member.Guard != null && member.Review == null && await tasks.BeginCallAsync(member.Guard, member.Id, "text", null, now, token);
            if (!valid) continue;
            accepted.Add(member);
        }
        if (accepted.Count == 0) return null;
        db.AiRequestBatches.Add(batch);
        foreach (var member in accepted) db.AiRequestBatchMembers.Add(new()
        { BatchId = batch.Id, Id = member.Id, TargetId = member.Guard?.TargetId ?? member.Review!.SubmissionId });
        await db.SaveChangesAsync(token); db.ChangeTracker.Clear();
        return new(batch.Id, accepted);
    }, token);

    public async Task CompleteAsync(AiBatchReceipt batch, IReadOnlyList<AiBatchResult> results, string outcome, string? errorCode,
        int nextSize, long now, long? retryAt, CancellationToken token,
        EnrichmentServiceAdmission? admission = null, EnrichmentServicePolicy? policy = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await CompleteOnceAsync(batch, results, outcome, errorCode, nextSize, now, retryAt, token, admission, policy);
                return;
            }
            catch (StorageBusyException) when (attempt < 2)
            {
                // Retry the same validated response after rollback, never the HTTP
                // request. Release the writer between attempts so other work drains.
                db.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), token);
            }
        }
    }

    private Task CompleteOnceAsync(AiBatchReceipt batch, IReadOnlyList<AiBatchResult> results, string outcome, string? errorCode,
        int nextSize, long now, long? retryAt, CancellationToken token,
        EnrichmentServiceAdmission? admission, EnrichmentServicePolicy? policy) => tasks.BatchTransactionAsync(async () =>
    {
        var record = await db.AiRequestBatches.AsNoTracking().SingleAsync(r => r.Id == batch.Id, token);
        if (record.FinishedAtUnixMs != null) return false;
        if (outcome is not ("Ready" or "Partial" or "Invalid" or "Rejected" or "Throttled" or "ServiceFailure"))
            throw new ArgumentException("Invalid AI batch outcome.");
        var registered = await db.AiRequestBatchMembers.AsNoTracking().Where(r => r.BatchId == batch.Id).ToArrayAsync(token);
        if (registered.Length != batch.Members.Count || batch.Members.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != registered.Length
            || batch.Members.Any(m => !registered.Any(r => r.Id == m.Id && r.TargetId == (m.Guard?.TargetId ?? m.Review?.SubmissionId)))
            || results.Any(r => !registered.Any(m => m.Id == r.Id)))
            throw new ArgumentException("AI batch members do not match the persisted request.");
        var supplied = results.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var savedMembers = 0;
        foreach (var member in batch.Members)
        {
            supplied.TryGetValue(member.Id, out var result);
            var state = "Failed";
            if (member.Guard is { } guard)
            {
                var saved = false;
                if (result?.Plan is { } plan)
                {
                    try { saved = await tasks.SavePlanAsync(guard, plan, now, token); }
                    catch (ArgumentException) { }
                }
                if (saved)
                {
                    savedMembers++;
                    await tasks.FinishCallAsync(guard, member.Id, EnrichmentCallResult.Succeeded, null, now, null, token);
                    await tasks.ReleaseBatchAsync(guard, now, token);
                    state = "Ready";
                }
                else
                {
                    var failureCode = result?.ErrorCode ?? errorCode ?? "batch.invalid_response";
                    var changed = await tasks.FinishCallAsync(guard, member.Id,
                        EnrichmentRecoveryPolicy.Failure(result?.FailureOutcome ?? outcome, failureCode), failureCode,
                        now, Math.Max(now + 1000, retryAt ?? now + 1000), token);
                    state = changed ? "Failed" : "Superseded";
                }
            }
            else if (member.Review is { } lease && result?.Review is { } review)
            {
                // A valid late review is preserved by the existing review writer as Applied=false when necessary.
                await reviews.StageValidatedAsync(lease, review, token);
                state = await reviews.ReadValidatedAsync(lease, token) != null ? "Ready" : "Superseded";
            }
            await db.AiRequestBatchMembers.Where(r => r.Id == member.Id && r.BatchId == batch.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Outcome, state), token);
        }
        if (record.Kind == "Expand")
        {
            // The same writer records completion and the policy. A late success
            // from before a failure cannot undo that failure's backoff.
            var sizing = await db.AiRequestBatchPolicies.SingleOrDefaultAsync(p => p.Kind == record.Kind && p.ConfigurationVersion == record.ConfigurationVersion, token);
            if (sizing == null)
            {
                sizing = new() { Kind = record.Kind, ConfigurationVersion = record.ConfigurationVersion };
                db.AiRequestBatchPolicies.Add(sizing);
            }
            if (outcome is "Invalid" or "Partial" || errorCode == "batch.timeout")
            { sizing.Size = 1; sizing.FastSuccesses = 0; sizing.LastFailureAtUnixMs = now; }
            else if (outcome == "Ready" && savedMembers == batch.Members.Count && record.StartedAtUnixMs > sizing.LastFailureAtUnixMs)
            {
                sizing.FastSuccesses = now - record.StartedAtUnixMs is >= 0 and < 60_000 ? Math.Min(3, sizing.FastSuccesses + 1) : 0;
                if (sizing.FastSuccesses >= 3) sizing.Size = 2;
            }
            else if (outcome != "Ready" || savedMembers != batch.Members.Count) sizing.FastSuccesses = 0;
            nextSize = sizing.Size;
            await db.SaveChangesAsync(token); db.ChangeTracker.Clear();
        }
        await db.AiRequestBatches.Where(r => r.Id == batch.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Outcome, outcome).SetProperty(r => r.NextSize, Math.Clamp(nextSize, 1, 10))
            .SetProperty(r => r.FinishedAtUnixMs, now), token);
        if (admission != null && policy != null)
            await scheduler.FinishServiceAsync(admission, outcome, errorCode, true, now, retryAt, policy, token);
        return true;
    }, token);

    public async Task<AiBatchStatistics> StatisticsAsync(CancellationToken token) => new(
        await db.AiRequestBatches.CountAsync(token), await db.AiRequestBatchMembers.CountAsync(token),
        await db.AiRequestBatchMembers.CountAsync(r => r.Outcome == "Ready", token),
        await db.AiRequestBatches.CountAsync(r => r.FinishedAtUnixMs == null, token));
}
