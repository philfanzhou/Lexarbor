namespace Lexarbor.Domain.Models;

public sealed record VocabularyMeaningLookup(string Word, IReadOnlyList<VocabularyLookupMeaning> Meanings);

public sealed record VocabularyLookupMeaning(
    string WordId,
    string Word,
    string WordKey,
    string BookId,
    string BookName,
    string MeaningId,
    string? PartOfSpeech,
    string Meaning,
    string MeaningKey);
