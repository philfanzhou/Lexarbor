namespace Lexarbor.Learning.Domain.Models;

/// <summary>A plan's coverage facts for the check-in calendar: status, creation and final update instants, optional reactivation gap.</summary>
public sealed record VocabularyCheckInPlan(Guid PlanId, VocabPlanStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, VocabularyPlanReactivation? Reactivation);
