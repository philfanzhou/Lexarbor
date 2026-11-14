namespace Lexarbor.Domain.Models;

public sealed record VocabularyFamilyQuery(string? WordId, string? Word = null, string? Meaning = null, string? PartOfSpeech = null);

public sealed record VocabularyFamilyLookup(string? WordId, bool Found, IReadOnlyList<VocabularyFamilyRelation> Items);

public sealed record VocabularyFamilyRelation(string RelationId, string Type, string Direction, string? UsageNote, string OriginKind,
    VocabularyFamilyWord Word, IReadOnlyList<VocabularyFamilyMeaning> Meanings);

public sealed record VocabularyFamilyWord(string WordId, string Word, string NormalizedWord, string? PhoneticUk, string? PhoneticUs);

public sealed record VocabularyFamilyMeaning(string BookId, string BookName, string MeaningId, string? PartOfSpeech,
    string Meaning, string MeaningKey, string? Example);
