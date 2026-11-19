namespace Lexarbor.Learning.Domain.Models;

public sealed record VocabularyFamilySources(int Page, int Size, int Total, IReadOnlyList<VocabularyFamilySourceSummary> Items);
public sealed record VocabularyFamilySourceSummary(string WordKey, string Word, DateTimeOffset LastReviewedAt, int AddableCount);
public sealed record VocabularyFamilyCandidates(string WordKey, string Word, IReadOnlyList<VocabularyFamilyCandidate> DerivedWords,
    IReadOnlyList<VocabularyFamilyCandidate> WordForms);
public sealed record VocabularyFamilyCandidate(string RelationId, string Type, string Direction, string? UsageNote, string Word,
    string WordKey, string Status, string? PreselectedMeaningId, bool MatchesLearnedMeaning, IReadOnlyList<VocabularyFamilyMeaning> Meanings)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDerived => Type is "derived_noun" or "derived_verb" or "derived_adjective" or "derived_adverb";
}

public sealed record VocabularyFamilyMeaning(string MeaningId, string? PartOfSpeech, string Meaning, string BookName, bool InNotebook, bool CanSelect);
public sealed record VocabularyFamilySelection(string? RelationId, string? MeaningId);
public sealed record VocabularyFamilyAddResult(string RelationId, string MeaningId, string Status);
