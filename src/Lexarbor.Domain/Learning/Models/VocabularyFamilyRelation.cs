using Lexarbor.Learning.Domain.Services;

namespace Lexarbor.Learning.Domain.Models;

public sealed record VocabularyFamilyRelation(string RelationId, string Type, string Direction, string? UsageNote,
    string Word, string WordKey, IReadOnlyList<VocabCatalogMeaning> Meanings, IReadOnlyList<VocabularyFamilyMeaningPair> MeaningPairs)
{
    public string? QueryWordId { get; init; }
    public bool IsDerived => Type is "derived_noun" or "derived_verb" or "derived_adjective" or "derived_adverb";

    public VocabularyCatalogReference From(VocabularyFamilyMeaningPair pair) => Direction == "outgoing" ? pair.Source : pair.Target;
    public VocabularyCatalogReference To(VocabularyFamilyMeaningPair pair) => Direction == "outgoing" ? pair.Target : pair.Source;

    public static bool ValidType(string? type) => type is "derived_noun" or "derived_verb" or "derived_adjective" or "derived_adverb"
        or "past_tense" or "past_participle" or "ing_form" or "third_person_singular" or "plural" or "comparative" or "superlative";
}
