using System.Text.Json;
using Lexarbor.Learning.Domain.Calendar;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyPlanStatsService(IVocabularyPlanStatsRepository repository,
    IVocabularyScopeEntryReader scopeReader, TimeProvider clock)
{
    public async Task<IReadOnlyList<VocabularyPlanStats>> GetAsync(IReadOnlyList<Guid> planIds,
        int tzOffsetMinutes = 0, CancellationToken cancellationToken = default)
    {
        if (planIds.Count is < 1 or > 100 || planIds.Contains(Guid.Empty))
            throw new ArgumentException("Send between one and 100 non-empty plan ids.", nameof(planIds));
        if (tzOffsetMinutes is < -720 or > 840) throw new ArgumentOutOfRangeException(nameof(tzOffsetMinutes));
        cancellationToken.ThrowIfCancellationRequested();
        var ids = planIds.Distinct().ToArray();
        var now = clock.GetUtcNow();
        var cache = new Dictionary<string, IReadOnlyList<CardKey>>(StringComparer.Ordinal);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            IReadOnlyList<VocabPlanModel> candidates;
            string signature;
            await using (var metadata = await repository.OpenAsync(ids, cancellationToken).ConfigureAwait(false))
            {
                candidates = metadata.Plans.ToArray();
                signature = Signature(candidates);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (candidates.Count == 0) return ids.Select(id => new VocabularyPlanStats(id, true)).ToArray();
            var scopes = new List<VocabularyPlanStatsScope>();
            foreach (var plan in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // JSON preserves ordered books and units without delimiter collisions.
                var scopeKey = JsonSerializer.Serialize(plan.Segments.Select(segment =>
                    new VocabPlanSegment(segment.BookId, segment.UnitIds is { Count: > 0 } ? segment.UnitIds : null)));
                if (!cache.TryGetValue(scopeKey, out var keys))
                {
                    var entries = await VocabularyPlanScopeEntry.ReadAsync(scopeReader, plan.Segments, cancellationToken).ConfigureAwait(false);
                    keys = entries.Select(item => new CardKey(item.Entry.NormalizedWord, item.Entry.MeaningKey)).ToArray();
                    cache.Add(scopeKey, keys);
                }
                var start = LocalDate(plan.CreatedAt, tzOffsetMinutes);
                var end = LocalDate(plan.Status == VocabPlanStatus.Active ? now : plan.UpdatedAt, tzOffsetMinutes);
                scopes.Add(new(plan.Id, plan.StudentId, keys, start, end));
            }
            await using var snapshot = await repository.OpenAsync(ids, cancellationToken).ConfigureAwait(false);
            if (signature != Signature(snapshot.Plans)) continue;
            var counts = scopes.Count == 0 ? new Dictionary<Guid, VocabularyPlanStatsCounts>()
                : await snapshot.ReadCountsAsync(scopes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var plans = snapshot.Plans.ToDictionary(p => p.Id);
            var windows = scopes.ToDictionary(s => s.PlanId);
            var results = new List<VocabularyPlanStats>(ids.Length);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!plans.TryGetValue(id, out var plan)) { results.Add(new(id, true)); continue; }
                var count = counts.GetValueOrDefault(id) ?? new VocabularyPlanStatsCounts(0, 0, 0);
                var window = windows[id];
                var days = Math.Max(1, window.End.DayNumber - window.Start.DayNumber + 1 - GapDays(snapshot, id, window, tzOffsetMinutes));
                results.Add(new(id, false, plan.StudentId, plan.Status.ToString(), plan.TotalWords,
                    count.LearnedWords, plan.TotalWords == 0 ? 0 : (double)count.LearnedWords / plan.TotalWords,
                    count.CheckInDays, days, (double)count.CheckInDays / days, count.PreviouslyLearnedWords));
            }
            return results;
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new VocabularyStatisticsUnavailableException();
    }

    private static string Signature(IReadOnlyList<VocabPlanModel> plans) => JsonSerializer.Serialize(
        plans.OrderBy(plan => plan.Id).Select(plan => new
        { plan.Id, plan.StudentId, plan.Version, plan.Status, plan.TotalWords, plan.CreatedAt, plan.UpdatedAt, plan.Segments }));

    private static DateOnly LocalDate(DateTimeOffset instant, int tzOffsetMinutes) =>
        LearningDay.From(instant);
    private static int GapDays(IVocabularyPlanStatsSnapshot snapshot, Guid planId, VocabularyPlanStatsScope window, int tzOffsetMinutes)
    {
        if (!snapshot.Reactivations.TryGetValue(planId, out var reactivation)) return 0;
        var first = LocalDate(reactivation.CompletedAt, tzOffsetMinutes).AddDays(1);
        var last = LocalDate(reactivation.ReactivatedAt, tzOffsetMinutes).AddDays(-1);
        if (first < window.Start) first = window.Start;
        if (last > window.End) last = window.End;
        return Math.Max(0, last.DayNumber - first.DayNumber + 1);
    }
}
