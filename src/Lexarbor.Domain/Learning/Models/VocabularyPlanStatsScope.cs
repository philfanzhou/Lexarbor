namespace Lexarbor.Learning.Domain.Models;

public sealed record VocabularyPlanStatsScope(Guid PlanId, Guid StudentId,
    IReadOnlyList<CardKey> Keys, DateOnly Start, DateOnly End);
