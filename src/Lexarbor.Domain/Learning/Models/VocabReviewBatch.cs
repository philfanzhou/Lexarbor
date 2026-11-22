namespace Lexarbor.Learning.Domain.Models;

/// <summary>A frozen queue of distinct learned words. Completed indexes the next card to review.</summary>
public sealed class VocabReviewBatch
{
    public Guid Id { get; set; }
    public int Target { get; set; }
    public List<Guid> RequestIds { get; set; } = [];
    public List<Guid> QuestionIds { get; set; } = [];
    public List<CardKey> Cards { get; set; } = [];
    public List<CardKey> Unavailable { get; set; } = [];
    public int Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public VocabExtraBatchSnapshot Snapshot() => new(Id, Target, Cards.Count, Completed, CompletedAt);
}
