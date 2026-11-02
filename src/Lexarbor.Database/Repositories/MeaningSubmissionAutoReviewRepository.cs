using System.Text.Json;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class MeaningSubmissionAutoReviewRepository(VocabularyDbContext db, TimeProvider clock)
    : IMeaningSubmissionAutoReviewRepository
{
    public Task<MeaningSubmissionReviewLease?> ClaimAsync(string owner, TimeSpan leaseDuration, CancellationToken token)
        => new UnitOfWork(db).ExecuteInTransactionAsync<MeaningSubmissionReviewLease?>(async () =>
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            // All pending rows participate, including rows never notified or queued.
            // Future failures cannot hide unqueued rows or other due submissions.
            var id = await (from submission in db.MeaningSubmissions.AsNoTracking()
                            join state in db.MeaningSubmissionAutoReviews.AsNoTracking() on submission.Id equals state.SubmissionId into states
                            from state in states.DefaultIfEmpty()
                            where (submission.Status == MeaningSubmissionStatus.Pending || state != null && state.ValidatedReviewJson != null)
                                && (state == null || state.NextAttemptAtUnixMs <= now && state.LeaseUntilUnixMs <= now)
                            orderby state == null ? 0 : state.NextAttemptAtUnixMs, submission.CreatedAtUnixMs, submission.Id
                            select submission.Id).FirstOrDefaultAsync(token);
            if (id == null) return null;
            var row = await db.MeaningSubmissionAutoReviews.SingleOrDefaultAsync(s => s.SubmissionId == id, token);
            if (row == null) { row = new() { SubmissionId = id }; db.MeaningSubmissionAutoReviews.Add(row); }
            if (row.ValidatedReviewJson == null) row.AttemptId = Guid.NewGuid().ToString("N");
            row.Owner = owner; row.Epoch++;
            row.LeaseUntilUnixMs = now + (long)leaseDuration.TotalMilliseconds;
            await db.SaveChangesAsync(token);
            return new(id, row.AttemptId, owner, row.Epoch);
        }, token);

    public Task<bool> IsCurrentAsync(MeaningSubmissionReviewLease lease, CancellationToken token)
    {
        if (!UnitOfWork.OwnsWriteContext(db))
            throw new InvalidOperationException("Review eligibility requires the submission writer context.");
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        return db.MeaningSubmissionAutoReviews.AsNoTracking().AnyAsync(s => s.SubmissionId == lease.SubmissionId
            && s.AttemptId == lease.AttemptId && s.Owner == lease.Owner && s.Epoch == lease.Epoch && s.LeaseUntilUnixMs > now, token);
    }

    public Task FinishAsync(MeaningSubmissionReviewLease lease, string? failureCode, TimeSpan? retryAfter, CancellationToken token)
        => new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            // Never mutate the newer owner's scheduling state, even after an old valid result was retained.
            var query = db.MeaningSubmissionAutoReviews.Where(s => s.SubmissionId == lease.SubmissionId
                && s.AttemptId == lease.AttemptId && s.Owner == lease.Owner && s.Epoch == lease.Epoch);
            var row = await query.AsNoTracking().SingleOrDefaultAsync(token);
            if (row == null) return false;
            if (failureCode != null && failureCode is not ("rate_limited" or "service_unavailable" or "request_rejected" or "response_too_large"
                or "invalid_response" or "service_timeout" or "connection_failed" or "result_commit_failed"))
                throw new ArgumentException("Unknown automatic review failure code.", nameof(failureCode));
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var conclusionSaved = await db.MeaningSubmissionAiReviews.AsNoTracking().AnyAsync(r => r.AttemptId == lease.AttemptId, token);
            var failures = failureCode == null ? row.ConsecutiveFailures : Math.Min(30, row.ConsecutiveFailures + 1);
            var delay = failureCode == null ? 0 : Math.Min(120_000L, 8_000L << Math.Min(4, failures - 1));
            if (failureCode != null && retryAfter is { } retry) delay = Math.Max(delay,
                (long)Math.Min(retry.TotalMilliseconds, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() - now));
            await query.ExecuteUpdateAsync(set => set.SetProperty(s => s.Owner, (string?)null)
                .SetProperty(s => s.LeaseUntilUnixMs, 0L).SetProperty(s => s.NextAttemptAtUnixMs, now + delay)
                .SetProperty(s => s.ConsecutiveFailures, failures)
                .SetProperty(s => s.ValidatedReviewJson, conclusionSaved ? null : row.ValidatedReviewJson)
                .SetProperty(s => s.LastFailureCode, failureCode ?? row.LastFailureCode)
                .SetProperty(s => s.LastFailureAtUnixMs, failureCode == null ? row.LastFailureAtUnixMs : now), token);
            return true;
        }, token);

    public async Task<MeaningSubmissionAiReview?> ReadValidatedAsync(MeaningSubmissionReviewLease lease, CancellationToken token)
    {
        var json = await db.MeaningSubmissionAutoReviews.AsNoTracking().Where(s => s.SubmissionId == lease.SubmissionId
            && s.AttemptId == lease.AttemptId && s.Owner == lease.Owner && s.Epoch == lease.Epoch)
            .Select(s => s.ValidatedReviewJson).SingleOrDefaultAsync(token);
        return json == null ? null : JsonSerializer.Deserialize<MeaningSubmissionAiReview>(json);
    }

    public Task StageValidatedAsync(MeaningSubmissionReviewLease lease, MeaningSubmissionAiReview review, CancellationToken token)
        => new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            if (review.AttemptId != lease.AttemptId || review.SubmissionId != lease.SubmissionId)
                throw new ArgumentException("Review does not match its attempt.", nameof(review));
            var json = JsonSerializer.Serialize(review);
            var query = db.MeaningSubmissionAutoReviews.Where(s => s.SubmissionId == lease.SubmissionId
                && s.AttemptId == lease.AttemptId && s.Owner == lease.Owner && s.Epoch == lease.Epoch);
            var previous = await query.AsNoTracking().Select(s => s.ValidatedReviewJson).SingleOrDefaultAsync(token);
            if (previous != null && previous != json) throw new InvalidOperationException("A staged review cannot change its payload.");
            await query.ExecuteUpdateAsync(set => set.SetProperty(s => s.ValidatedReviewJson, json), token);
            return true;
        }, token);

    public async Task<MeaningSubmissionReviewBacklog> ReadBacklogAsync(CancellationToken token)
    {
        var pending = db.MeaningSubmissions.Where(s => s.Status == MeaningSubmissionStatus.Pending);
        var count = await pending.CountAsync(token);
        var latest = await db.MeaningSubmissionAutoReviews.AsNoTracking().Where(s => s.LastFailureAtUnixMs != null)
            .OrderByDescending(s => s.LastFailureAtUnixMs).ThenBy(s => s.SubmissionId).FirstOrDefaultAsync(token);
        var next = await db.MeaningSubmissionAutoReviews.Where(s => pending.Any(p => p.Id == s.SubmissionId))
            .Select(s => (long?)Math.Max(s.NextAttemptAtUnixMs, s.LeaseUntilUnixMs)).MinAsync(token);
        return new(count, latest?.LastFailureCode, At(latest?.LastFailureAtUnixMs), At(next));
    }
    private static DateTimeOffset? At(long? value) => value is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}
