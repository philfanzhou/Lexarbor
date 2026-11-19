namespace Lexarbor.Learning.Domain.Models;

/// <summary>A resumable, finite extra-learning batch with a size captured at its start.</summary>
public sealed class VocabExtraBatch
{
    public Guid Id { get; set; }
    public List<Guid> RequestIds { get; set; } = [];
    public int Target { get; set; }
    public int Required { get; set; }
    public int PlanVersion { get; set; }
    public bool AllRemainingSelected { get; set; }
    public List<VocabDailyWord> Words { get; set; } = [];
    public List<string> LearnedWords { get; set; } = [];
    public List<Guid> QuestionIds { get; set; } = [];
    public DateTimeOffset? CompletedAt { get; set; }

    public VocabExtraBatchSnapshot Snapshot() => new(Id, Target, Required, LearnedWords.Count, CompletedAt);
}
