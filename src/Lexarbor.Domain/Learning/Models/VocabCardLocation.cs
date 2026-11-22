namespace Lexarbor.Learning.Domain.Models;

/// <summary>
/// The catalog location a card is reviewed at, recorded when the card is created and never moved by later
/// questions. <see cref="Source"/> is null when history did not record it.
/// </summary>
public sealed record VocabCardLocation(
    Guid StudentId,
    CardKey Key,
    string BookId,
    string WordId,
    string MeaningId,
    VocabularySourceKind? Source,
    Guid? SourceQuestionId,
    DateTimeOffset RecordedAt);
