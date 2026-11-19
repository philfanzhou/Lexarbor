namespace Lexarbor.Learning.Domain.Models;
public sealed class MemoryReviewLogModel
{
    public required Guid StudentId { get; init; }

    public required string WordKey { get; init; }

    public required string MeaningKey { get; init; }

    public ReviewRating Rating { get; init; }

    public required DateTimeOffset ReviewedAt { get; init; }

    /// <summary>Actual input state; null for historical logs without state evidence.</summary>
    public MemoryCardState? StateBefore { get; init; }

    /// <summary>Actual scheduled result state; null for historical logs without state evidence.</summary>
    public MemoryCardState? StateAfter { get; init; }

    public double? StabilityBefore { get; init; }

    public double? DifficultyBefore { get; init; }

    public DateTimeOffset? DueBefore { get; init; }

    public double StabilityAfter { get; init; }

    public double DifficultyAfter { get; init; }

    public DateTimeOffset DueAfter { get; init; }

    public int ElapsedDays { get; init; }
}
