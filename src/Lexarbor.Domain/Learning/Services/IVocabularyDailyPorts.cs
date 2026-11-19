using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

public sealed record VocabularyScopeEntry(
    string WordId,
    string NormalizedWord,
    string MeaningId,
    string MeaningKey,
    string? UnitId);
public interface IVocabularyScopeEntryReader
{
    Task<IReadOnlyList<VocabularyScopeEntry>> ReadOrderedScopeEntriesAsync(
        string bookId, IReadOnlyList<string>? unitIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Learning mode sent to Lexarbor's on-demand question routes. New words and extra words
/// use <see cref="New"/>; every review (daily, learning-group fill and free review) uses
/// <see cref="Review"/>. The mode follows the saved question kind, not the UI mode.
/// </summary>
public enum VocabGenerationMode
{
    New,
    Review,
}

/// <summary>
/// Question request for one exact meaning target. Lexarbor chooses the question type:
/// new mode only produces EnglishToChinese; review mode draws among the types it can generate.
/// UnitId limits choice candidates to that unit (the caller retries once without it).
/// </summary>
public sealed record VocabQuestionSpec(
    string WordId,
    string BookId,
    string MeaningId,
    string? UnitId,
    VocabGenerationMode Mode,
    VocabQuestionRelation? Relation = null);

public sealed record VocabQuestionRelation(string SourceWordId, string Type,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RelationId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? SourceMeaningId = null);

/// <summary>
/// A validated provider question. Choice directions carry four options with exactly one
/// correct; FillLetters carries the exact received pattern (null = blank) and answer word and
/// no options. Correctness and answers stay server-side in the saved snapshot.
/// </summary>
public sealed record VocabGeneratedQuestion(
    VocabQuestionDirection Direction,
    string Prompt,
    IReadOnlyList<VocabGeneratedOption> Options,
    IReadOnlyList<string?>? Pattern = null,
    string? AnswerWord = null,
    string? AnchorWord = null,
    string? PartOfSpeech = null,
    string? RelationType = null);

public sealed record VocabGeneratedOption(string Text, bool IsCorrect);

/// <summary>
/// Question generation port: the implementation calls Lexarbor's on-demand learning-question
/// routes. Null means the provider explicitly reported the target or every allowed type as
/// unavailable; the caller retries once without unitId when one was sent, then skips the meaning.
/// Every other failure, including an invalid response, throws VocabCatalogUnavailableException
/// (503, no state change). Cancellation propagates. Correct answers never leave the domain layer.
/// </summary>
public interface IVocabularyQuestionGenerator
{
    Task<VocabGeneratedQuestion?> CreateAsync(VocabQuestionSpec spec, CancellationToken cancellationToken = default);
}
