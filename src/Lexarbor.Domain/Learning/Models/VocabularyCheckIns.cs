namespace Lexarbor.Learning.Domain.Models;

/// <summary>Check-in calendar snapshot: requested dates plus cumulative check-in days and the current streak.</summary>
public sealed record VocabularyCheckIns(Guid StudentId, DateOnly Today, DateOnly? FirstPlanDate,
    int TotalCheckInDays, int CurrentStreak, IReadOnlyList<VocabularyCheckInDay> Days);
