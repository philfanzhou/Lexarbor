namespace Lexarbor.Learning.Domain.Models;

public sealed record VocabularyWordListItem(string WordKey, string Word, bool SnapshotIncomplete,
    IReadOnlyList<VocabularyWordListItem.MeaningEntry> Meanings)
{
    /// <summary>One learned meaning; stage is learning, familiar or mastered.</summary>
    public sealed record MeaningEntry(string MeaningKey, string Meaning, bool SnapshotIncomplete, string Stage,
        DateTimeOffset DueAt, int Reps, int Lapses, DateTimeOffset FirstLearnedAt, DateTimeOffset LastReviewedAt)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? LatestLinkedQuestionType { get; init; }
    }
}
