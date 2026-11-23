namespace Lexarbor.Learning.Domain.Models;

/// <summary>The daily assignments and their progress, including locations that could not be issued.</summary>
public sealed class VocabDailyTasks
{
    public bool LegacyDay { get; set; }
    public List<VocabExtraBatch> ExtraBatches { get; set; } = [];
    public List<VocabReviewBatch> ExtraReviewBatches { get; set; } = [];
    public int NewRequired { get; set; }
    public List<VocabDailyWord> NewWords { get; set; } = [];
    public List<VocabDailyWord> UnavailableNewMeanings { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public List<CardKey> NewKeys => NewWords.Select(word => new CardKey(word.WordKey, word.MeaningKey)).ToList();
    public List<string> LearnedWords { get; set; } = [];
    public List<CardKey> FillReviews { get; set; } = [];
    public List<CardKey> FillReviewed { get; set; } = [];
    public List<Guid> QuestionIds { get; set; } = [];

    public static int WordCount(IEnumerable<CardKey> keys) => keys.Select(key => key.WordKey).Distinct(StringComparer.Ordinal).Count();

    public static int CompletedWords(IEnumerable<CardKey> required, IReadOnlyCollection<CardKey> completed) =>
        required.GroupBy(key => key.WordKey, StringComparer.Ordinal).Count(word => word.All(completed.Contains));
}
