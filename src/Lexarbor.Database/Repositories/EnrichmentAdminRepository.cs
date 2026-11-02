using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class EnrichmentAdminRepository(VocabularyDbContext context) : IEnrichmentAdminRepository
{
    public Task<EnrichmentAdminPage<EnrichmentAdminTask>> ListAsync(EnrichmentAdminFilter filter,
        EnrichmentAdminConfiguration configuration, long now, CancellationToken token) => ReadAsync(async () =>
    {
        Bounds(filter.Page, filter.Size);
        var query = Filter(EnrichmentAdminQueries.Rows(context, configuration, now, true), filter);
        var count = await query.CountAsync(token);
        var rows = await query.OrderBy(r => r.WordId).ThenBy(r => r.MeaningId).ThenBy(r => r.Locale).ThenBy(r => r.Step).ThenBy(r => r.Id)
            .Skip((filter.Page - 1) * filter.Size).Take(filter.Size).ToArrayAsync(token);
        var result = new List<EnrichmentAdminTask>();
        foreach (var row in rows) result.Add(await TaskAsync(row, token));
        return new EnrichmentAdminPage<EnrichmentAdminTask>(result, count, filter.Page, filter.Size);
    }, token);

    public Task<EnrichmentAdminDetail?> DetailAsync(string id, int historyPage, int historySize,
        EnrichmentAdminConfiguration configuration, long now, CancellationToken token) => ReadAsync<EnrichmentAdminDetail?>(async () =>
    {
        Bounds(historyPage, historySize);
        var row = await EnrichmentAdminQueries.Rows(context, configuration, now, true).SingleOrDefaultAsync(r => r.Id == id, token);
        if (row == null) return null;
        var rounds = await PageAsync(context.EnrichmentRounds.AsNoTracking().Where(r => r.TargetId == id).OrderByDescending(r => r.Number),
            historyPage, historySize, r => Round(r), token);
        var calls = await PageAsync(context.EnrichmentCalls.AsNoTracking().Where(r => r.TargetId == id)
            .OrderByDescending(r => r.StartedAtUnixMs).ThenBy(r => r.Id), historyPage, historySize,
            r => new EnrichmentAdminCall(r.Id, r.Round, r.Service, r.Result.ToString(), SafeError(r.ErrorCode), r.StartedAtUnixMs, r.FinishedAtUnixMs), token);
        var waits = await PageAsync(context.EnrichmentWaits.AsNoTracking().Where(r => r.TargetId == id)
            .OrderByDescending(r => r.RecordedAtUnixMs).ThenBy(r => r.Id), historyPage, historySize,
            r => new EnrichmentAdminWait(r.Round, r.Reason.ToString(), SafeError(r.ErrorCode), r.RecordedAtUnixMs, r.NextAttemptAtUnixMs), token);
        var cleanup = await PageAsync(Cleanup(row).OrderByDescending(r => r.AuthorizationEpoch).ThenByDescending(r => r.CreatedAtUnixMs).ThenBy(r => r.AssetId),
            historyPage, historySize, r => new EnrichmentAdminCleanupHistory(r.AuthorizationEpoch, r.State.ToString(), r.Attempts,
                r.NextAttemptAtUnixMs, CleanupError(r.ErrorCode), r.CreatedAtUnixMs, r.UpdatedAtUnixMs), token);
        return new(await TaskAsync(row, token), rounds, calls, waits, cleanup);
    }, token);

    public Task<EnrichmentAdminSummary> SummaryAsync(EnrichmentAdminFilter filter,
        EnrichmentAdminConfiguration configuration, long now, CancellationToken token) => ReadAsync(async () =>
    {
        var query = Filter(EnrichmentAdminQueries.Rows(context, configuration, now, false), filter);
        var counts = await query.GroupBy(r => new { r.State, r.WaitReason })
            .Select(g => new EnrichmentAdminCount(g.Key.State, g.Key.WaitReason, g.Count())).ToArrayAsync(token);
        var total = await query.CountAsync(token);
        var completed = await query.CountAsync(r => !r.Missing, token);
        var unqueued = await query.CountAsync(r => r.Missing && r.Id == null, token);
        var persisted = await Filter(EnrichmentAdminQueries.Rows(context, configuration, now, true), filter).CountAsync(token);
        var services = new List<EnrichmentAdminService>();
        foreach (var (service, id, version) in new[] { ("text", configuration.TextServiceId, configuration.TextServiceVersion),
            ("tts", configuration.TtsServiceId, configuration.TtsServiceVersion) })
        {
            var s = await context.EnrichmentServices.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.ConfigurationVersion == version, token);
            services.Add(new(service, s?.Paused ?? false, s?.Blocked ?? false, s?.ConsecutiveFailures ?? 0,
                s?.ThrottleSeconds ?? 0, s?.NextAttemptAtUnixMs is > 0 ? s.NextAttemptAtUnixMs : null));
        }
        return new EnrichmentAdminSummary(total, completed, total - completed, unqueued, persisted,
            counts.OrderBy(c => c.State, StringComparer.Ordinal).ThenBy(c => c.WaitReason, StringComparer.Ordinal).ToArray(), services);
    }, token);

    private static IQueryable<EnrichmentAdminRow> Filter(IQueryable<EnrichmentAdminRow> query, EnrichmentAdminFilter filter)
    {
        if (filter.WordId != null) query = query.Where(r => r.WordId == filter.WordId);
        if (filter.MeaningId != null) query = query.Where(r => r.MeaningId == filter.MeaningId);
        if (filter.Locale != null) query = query.Where(r => r.Locale == filter.Locale);
        if (filter.State != null) query = query.Where(r => r.State == filter.State);
        if (filter.Step != null) query = query.Where(r => r.Step == filter.Step);
        return query;
    }
    private IQueryable<AudioInvalidationEntity> Cleanup(EnrichmentAdminRow row) => context.AudioInvalidations.AsNoTracking()
        .Where(i => row.MeaningId != null && i.WordId == row.WordId && i.MeaningId == row.MeaningId && i.Locale == AudioLocales.FromCode(row.Locale));

    private async Task<EnrichmentAdminTask> TaskAsync(EnrichmentAdminRow row, CancellationToken token)
    {
        var cleanup = Cleanup(row);
        var groups = await cleanup.GroupBy(i => i.State).Select(g => new { State = g.Key, Count = g.Count() }).ToArrayAsync(token);
        var latest = await cleanup.Where(i => i.State != AudioInvalidationState.Observed)
            .OrderByDescending(i => i.AuthorizationEpoch).ThenByDescending(i => i.CreatedAtUnixMs).ThenBy(i => i.AssetId).FirstOrDefaultAsync(token);
        var summary = new EnrichmentAdminCleanup(groups.Where(g => g.State == AudioInvalidationState.Waiting).Sum(g => g.Count),
            groups.Where(g => g.State == AudioInvalidationState.Claimed).Sum(g => g.Count), groups.Where(g => g.State == AudioInvalidationState.Observed).Sum(g => g.Count),
            await cleanup.SumAsync(i => (int?)i.Attempts, token) ?? 0, latest?.NextAttemptAtUnixMs, CleanupError(latest?.ErrorCode));
        return new(row.Id!, row.WordId, row.Word, row.MeaningId, row.Meaning, row.Locale, row.Step, row.Applicable, row.Missing,
            row.InheritsDefault, row.State, row.WaitReason, new(row.Number, row.RoundState!, row.RequestCount, row.ConsumedFailures,
                row.FailureBudget, row.WaitReason, SafeError(row.ErrorCode), row.NextAttemptAtUnixMs, row.CreatedAtUnixMs, row.UpdatedAtUnixMs), summary);
    }
    private static EnrichmentAdminRound Round(EnrichmentRoundEntity r) => new(r.Number, r.State.ToString(), r.RequestCount, r.ConsumedFailures,
        r.FailureBudget, r.WaitReason?.ToString(), SafeError(r.ErrorCode), r.NextAttemptAtUnixMs, r.CreatedAtUnixMs, r.UpdatedAtUnixMs);
    // Fixed summaries, never arbitrary persisted strings (even legacy/manual data).
    private static string? CleanupError(string? code) => code == null ? null : code switch
    { "audio.invalidation_referenced" => "audio.invalidation_referenced", "audio.invalidation_expired" => "audio.invalidation_expired", _ => "audio.invalidation_failed" };
    private static string? SafeError(string? code) => code == null ? null : code switch
    {
        "preparation.failed" or "fields.candidates_missing" or "fields.equivalent_meaning" or "fields.plan_too_large"
            or "text.outcome_unknown" or "tts.outcome_unknown" => code,
        _ when code.EndsWith(".throttled", StringComparison.Ordinal) => "provider.throttled",
        _ when code.Contains("unsupported", StringComparison.Ordinal) => "provider.capability_unsupported",
        _ when code.Contains("credentials", StringComparison.Ordinal) || code.Contains("balance", StringComparison.Ordinal) => "provider.configuration_rejected",
        _ => "enrichment.failed"
    };
    private static void Bounds(int page, int size)
    { if (page is < 1 or > 1000000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page)); }
    private static async Task<EnrichmentAdminPage<T>> PageAsync<TEntity, T>(IQueryable<TEntity> query, int page, int size,
        Func<TEntity, T> map, CancellationToken token)
    {
        var count = await query.CountAsync(token);
        var items = await query.Skip((page - 1) * size).Take(size).ToArrayAsync(token);
        return new(items.Select(map).ToArray(), count, page, size);
    }
    private async Task<T> ReadAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        if (context.Database.CurrentTransaction != null) throw new InvalidOperationException("Admin queries require a read-only snapshot.");
        await context.Database.OpenConnectionAsync(token);
        try
        {
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, token);
            var result = await action(); await transaction.CommitAsync(token); return result;
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }
}
