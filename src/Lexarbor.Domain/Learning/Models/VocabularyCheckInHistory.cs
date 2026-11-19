namespace Lexarbor.Learning.Domain.Models;

/// <summary>All plans of one student and the distinct local dates of their completed plan days.</summary>
public sealed record VocabularyCheckInHistory(bool StudentExists, IReadOnlyList<VocabularyCheckInPlan> Plans,
    IReadOnlyCollection<DateOnly> CheckedInDates);
