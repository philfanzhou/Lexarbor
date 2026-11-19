namespace Lexarbor.Learning.Domain.Models;

public sealed class MemoryCardModel
{
    public required Guid StudentId { get; init; }

    public required string WordKey { get; init; }

    public required string MeaningKey { get; init; }

    public MemoryCardState State { get; set; }

    public double? Stability { get; set; }

    public double? Difficulty { get; set; }

    public DateTimeOffset DueAt { get; set; }

    public int Reps { get; set; }

    public int Lapses { get; set; }

    public DateTimeOffset? LastReviewedAt { get; set; }

    public DateTimeOffset FirstLearnedAt { get; set; }

    public Guid? FirstLearnedPlanId { get; set; }

    public int? LearningStep { get; set; }

    public int Version { get; set; }
}
