using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Service;

internal sealed record VocabularyCheckInResponse(bool Success, VocabularyCheckInResponse.DataDto Data)
{
    internal sealed record DataDto(Guid StudentId, DateOnly Today, DateOnly? FirstPlanDate, int TotalCheckInDays,
        int CurrentStreak, IReadOnlyList<DayDto> Days);

    internal sealed record DayDto(DateOnly Date, string State);

    public static VocabularyCheckInResponse From(VocabularyCheckIns checkIns) => new(true, new DataDto(
        checkIns.StudentId, checkIns.Today, checkIns.FirstPlanDate, checkIns.TotalCheckInDays, checkIns.CurrentStreak,
        checkIns.Days.Select(day => new DayDto(day.Date, day.State)).ToList()));
}
