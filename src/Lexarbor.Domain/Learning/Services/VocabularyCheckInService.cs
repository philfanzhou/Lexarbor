using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

using Lexarbor.Learning.Domain.Calendar;

namespace Lexarbor.Learning.Domain.Services;
public sealed class VocabularyCheckInService(IVocabularyCheckInRepository repository, TimeProvider clock)
{
    public const int MaxRangeDays = 62;
    public const string CheckedIn = "checkedIn";
    public const string Missed = "missed";
    public const string NoPlan = "noPlan";
    public const string Pending = "pending";
    public const string Future = "future";

    public async Task<VocabularyCheckIns?> GetAsync(Guid studentId, DateOnly from, DateOnly to,
        int tzOffsetMinutes = 0, CancellationToken cancellationToken = default)
    {
        if (studentId == Guid.Empty) throw new ArgumentException("Student id is required.", nameof(studentId));
        if (tzOffsetMinutes is < -720 or > 840) throw new ArgumentOutOfRangeException(nameof(tzOffsetMinutes));
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
            throw new ArgumentOutOfRangeException(nameof(to), "Send a date range of at most 62 days.");
        cancellationToken.ThrowIfCancellationRequested();
        var today = LocalDate(clock.GetUtcNow(), tzOffsetMinutes);
        var history = await repository.ReadAsync(studentId, cancellationToken).ConfigureAwait(false);
        if (!history.StudentExists) return null;

        var coverage = history.Plans.Select(plan => Coverage(plan, today, tzOffsetMinutes)).ToList();
        var checkedIn = history.CheckedInDates.ToHashSet();
        string State(DateOnly date) =>
            date > today ? Future
            : checkedIn.Contains(date) ? CheckedIn
            : !coverage.Any(item => item.Covers(date)) ? NoPlan
            : date == today ? Pending : Missed;

        var days = new List<VocabularyCheckInDay>(to.DayNumber - from.DayNumber + 1);
        for (var date = from; date <= to; date = date.AddDays(1)) days.Add(new(date, State(date)));

        DateOnly? firstPlanDate = coverage.Count == 0 ? null : coverage.Min(item => item.Start);
        var earliest = new[] { firstPlanDate ?? DateOnly.MaxValue, checkedIn.Count == 0 ? DateOnly.MaxValue : checkedIn.Min() }.Min();
        var streak = 0;
        for (var date = checkedIn.Contains(today) ? today : today.AddDays(-1); date >= earliest; date = date.AddDays(-1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = State(date);
            if (state == CheckedIn) streak++;
            else if (state == Missed) break;
        }

        return new VocabularyCheckIns(studentId, today, firstPlanDate, checkedIn.Count, streak, days);
    }

    private static DateOnly LocalDate(DateTimeOffset instant, int tzOffsetMinutes) =>
        LearningDay.From(instant);

    private static PlanCoverage Coverage(VocabularyCheckInPlan plan, DateOnly today, int tzOffsetMinutes)
    {
        var start = LocalDate(plan.CreatedAt, tzOffsetMinutes);
        var end = plan.Status == VocabPlanStatus.Active ? today : LocalDate(plan.UpdatedAt, tzOffsetMinutes);
        if (plan.Reactivation is not { } reactivation) return new(start, end, null, null);
        // The days between completion and reactivation of a restored plan are not covered (ADR-0019 continuous plans).
        return new(start, end, LocalDate(reactivation.CompletedAt, tzOffsetMinutes).AddDays(1),
            LocalDate(reactivation.ReactivatedAt, tzOffsetMinutes).AddDays(-1));
    }

    private sealed record PlanCoverage(DateOnly Start, DateOnly End, DateOnly? GapStart, DateOnly? GapEnd)
    {
        public bool Covers(DateOnly date) => date >= Start && date <= End
            && !(GapStart is { } gapStart && GapEnd is { } gapEnd && date >= gapStart && date <= gapEnd);
    }
}
