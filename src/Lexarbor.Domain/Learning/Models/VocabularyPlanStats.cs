namespace Lexarbor.Learning.Domain.Models;

public sealed record VocabularyPlanStats(Guid PlanId, bool Missing, Guid? StudentId = null,
    string? Status = null, int? TotalWords = null, int? LearnedWords = null,
    double? LearningProgress = null, int? CheckInDays = null, int? ElapsedDays = null,
    double? CheckInRate = null, int? PreviouslyLearnedWords = null);
