namespace Lexarbor.Learning.Domain.Models;

public sealed record VocabularyOverview(
    Guid StudentId, DateTimeOffset RequestNow, DateOnly LocalDate, int TzOffsetMinutes,
    int VocabularySize, VocabularyOverviewStages Stages, int DueReviews, VocabPlanModel? Plan);
