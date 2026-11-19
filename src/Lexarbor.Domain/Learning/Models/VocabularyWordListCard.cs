namespace Lexarbor.Learning.Domain.Models;

// Internal query projection of one memory card.
public sealed record VocabularyWordListCard(string WordKey, string MeaningKey, double? Stability, DateTimeOffset DueAt,
    int Reps, int Lapses, DateTimeOffset FirstLearnedAt, DateTimeOffset LastReviewedAt);
