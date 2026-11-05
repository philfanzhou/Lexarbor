namespace Lexarbor.Domain.Models;

public sealed record VocabularyRelationType(string Code, string Description);

public static class VocabularyRelationTypes
{
    public static IReadOnlyList<VocabularyRelationType> All { get; } = Array.AsReadOnly(new[]
    {
        new VocabularyRelationType("past_tense", "Past tense"),
        new VocabularyRelationType("past_participle", "Past participle"),
        new VocabularyRelationType("ing_form", "-ing form"),
        new VocabularyRelationType("third_person_singular", "Simple present third-person singular"),
        new VocabularyRelationType("plural", "Noun plural"),
        new VocabularyRelationType("comparative", "Comparative"),
        new VocabularyRelationType("superlative", "Superlative"),
        new VocabularyRelationType("derived_noun", "Derived noun"),
        new VocabularyRelationType("derived_verb", "Derived verb"),
        new VocabularyRelationType("derived_adjective", "Derived adjective"),
        new VocabularyRelationType("derived_adverb", "Derived adverb")
    });

    public static bool IsValid(string type) => All.Any(item => item.Code == type);
}

/// <summary>One directed word-level fact, independent of books and grammatical roles.</summary>
public sealed record VocabularyRelationModel(
    string Id, string SourceWordId, string TargetWordId, string Type, string? UsageNote,
    string Status, string? ReviewReason, string OriginKind, string? OriginReference,
    long Version, long CreatedAtUnixMs, long UpdatedAtUnixMs, long? LastConfirmedAtUnixMs);

public sealed record VocabularyRelationInput(string SourceWordId, string TargetWordId, string Type,
    string? UsageNote = null, bool Confirm = false);
public sealed record VocabularyRelationQuery(string Direction = "both", string? Type = null,
    string? Status = null, int Page = 1, int Size = 20);
public sealed record VocabularyRelationPage(IReadOnlyList<VocabularyRelationModel> Items, int TotalCount, int TotalPage);
public sealed record VocabularyRelationCreation(VocabularyRelationModel Relation, bool Created);
public sealed record VocabularyRelationBatchResult(IReadOnlyList<VocabularyRelationModel> Items, int Created, int Reused);
