using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class MeaningSubmissionReviewAdminRepository(VocabularyDbContext context, TimeProvider time)
    : IMeaningSubmissionReviewAdminRepository
{
    public async Task<(List<MeaningSubmissionReviewEntry> Items, int TotalCount)> PageAsync(
        MeaningSubmissionReviewQuery query, CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction != null) throw new InvalidOperationException("Review queries require a read-only snapshot.");
        var wasOpen = context.Database.GetDbConnection().State == System.Data.ConnectionState.Open;
        if (!wasOpen) await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, cancellationToken);
            var rows = from review in context.MeaningSubmissionAiReviews.AsNoTracking()
                       join submission in context.MeaningSubmissions.AsNoTracking() on review.SubmissionId equals submission.Id
                       join check in context.MeaningSubmissionReviewChecks.AsNoTracking() on review.Id equals check.RecordId into checks
                       from check in checks.DefaultIfEmpty()
                       where (query.Verdict == null || review.Verdict == query.Verdict)
                          && (query.Applied == null || review.Applied == query.Applied)
                          && (query.RecordId == null || review.Id == query.RecordId)
                          && (query.Checked == null || (check != null) == query.Checked)
                       select new { review, submission, CheckedAt = check == null ? (long?)null : check.CheckedAtUnixMs };
            var total = await rows.CountAsync(cancellationToken);
            var page = await rows.OrderByDescending(row => row.review.ReviewedAtUnixMs).ThenBy(row => row.review.Id)
                .Skip((query.Page - 1) * query.Size).Take(query.Size).ToListAsync(cancellationToken);
            var entries = page.Select(row => new MeaningSubmissionReviewEntry(row.review.Id, row.review.SubmissionId,
                row.submission.Source, row.submission.Word, row.submission.PartOfSpeech, row.submission.Meaning, row.submission.Example,
                row.review.Verdict, row.review.PartOfSpeech, row.review.Meaning, row.review.PhoneticUk, row.review.PhoneticUs,
                row.review.Comment, row.review.Model, DateTimeOffset.FromUnixTimeMilliseconds(row.review.ReviewedAtUnixMs),
                row.review.Applied, row.CheckedAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null,
                row.review.Applied && row.review.Verdict != "reject" ? row.submission.ResultWordId : null)).ToList();
            await transaction.CommitAsync(cancellationToken);
            return (entries, total);
        }
        finally { if (!wasOpen) await context.Database.CloseConnectionAsync(); }
    }

    public Task<MeaningSubmissionReviewCheck> CheckAsync(string recordId, CancellationToken cancellationToken = default) =>
        new UnitOfWork(context).ExecuteInTransactionAsync(async () =>
        {
            if (!await context.MeaningSubmissionAiReviews.AsNoTracking().AnyAsync(row => row.Id == recordId, cancellationToken))
                throw new ResourceNotFoundException("AI review record was not found.");
            // No UPDATE of the conclusion or the first check time, even for a replay from another context.
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO meaning_submission_review_check (record_id, checked_at_unix_ms) VALUES ({recordId}, {time.GetUtcNow().ToUnixTimeMilliseconds()}) ON CONFLICT(record_id) DO NOTHING", cancellationToken);
            var check = await context.MeaningSubmissionReviewChecks.AsNoTracking().SingleAsync(row => row.RecordId == recordId, cancellationToken);
            return new MeaningSubmissionReviewCheck(recordId, DateTimeOffset.FromUnixTimeMilliseconds(check.CheckedAtUnixMs));
        }, cancellationToken);
}
