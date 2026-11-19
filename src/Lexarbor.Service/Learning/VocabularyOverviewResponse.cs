using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Service;

internal sealed record VocabularyOverviewResponse(bool Success, VocabularyOverviewResponse.DataDto Data)
{
    internal sealed record DataDto(Guid StudentId, DateTimeOffset RequestNow, DateOnly LocalDate,
        int TzOffsetMinutes, int VocabularySize, VocabularyOverviewStages Stages, int DueReviews, PlanDto? Plan);

    /// <summary>BookId / UnitIds mirror the first segment for consumers that predate multi-book scopes.</summary>
    internal sealed record PlanDto(Guid Id, Guid StudentId, string BookId, List<string>? UnitIds,
        int DailyNewWords, string Controller, Guid? ControllerTeacherId, string Status, DateOnly StartDate,
        int TotalWords, int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<SegmentDto> Segments);

    internal sealed record SegmentDto(string BookId, List<string>? UnitIds);

    public static VocabularyOverviewResponse From(VocabularyOverview overview)
    {
        var plan = overview.Plan;
        return new(true, new DataDto(overview.StudentId, overview.RequestNow, overview.LocalDate,
            overview.TzOffsetMinutes, overview.VocabularySize, overview.Stages, overview.DueReviews,
            plan is null ? null : new PlanDto(plan.Id, plan.StudentId, plan.BookId, plan.UnitIds,
                plan.DailyNewWords, plan.Controller.ToString(), plan.ControllerTeacherId, plan.Status.ToString(),
                plan.StartDate, plan.TotalWords, plan.Version, plan.CreatedAt, plan.UpdatedAt,
                plan.Segments.Select(segment => new SegmentDto(segment.BookId, segment.UnitIds)).ToList())));
    }
}
