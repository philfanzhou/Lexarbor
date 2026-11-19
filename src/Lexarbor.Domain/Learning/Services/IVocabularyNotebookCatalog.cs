using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

/// <summary>One meaning of a word in an enabled Lexarbor book; keys are Lexarbor's own comparison keys.</summary>
public sealed record VocabCatalogMeaning(
    string WordId,
    string Word,
    string WordKey,
    string? PhoneticUk,
    string? PhoneticUs,
    string BookId,
    string BookName,
    string MeaningId,
    string? PartOfSpeech,
    string Meaning,
    string MeaningKey);

public enum VocabSubmissionStatus
{
    Pending,
    Approved,
    Rejected,
}
public sealed record VocabMeaningSubmission(
    string Id,
    VocabSubmissionStatus Status,
    string Word,
    string? PartOfSpeech,
    string Meaning,
    string WordKey,
    string MeaningKey,
    bool DefinitionKept,
    string? ResultBookId,
    string? ResultWordId,
    string? ResultMeaningId,
    string? ResultWord,
    string? ResultPartOfSpeech,
    string? ResultMeaning,
    string? ResultWordKey,
    string? ResultMeaningKey,
    string? ReviewComment = null);
public interface IVocabularyNotebookCatalog
{
    Task<IReadOnlyList<VocabCatalogMeaning>> SearchAsync(string keyword, CancellationToken cancellationToken = default);

    Task<VocabMeaningSubmission> SubmitAsync(string word, string? partOfSpeech, string meaning, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VocabMeaningSubmission>> LookupAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
}
public interface IVocabularyNotebookSource
{
    Task<(VocabularyPlanScopeEntry Location, VocabNotebookFamilySource Source)?> ReadVerifiedFamilyAsync(
        Guid studentId, CardKey key, CancellationToken cancellationToken);

    /// <summary>The student's ready items in the order they were added; pending and rejected items are not learned.</summary>
    Task<IReadOnlyList<VocabularyPlanScopeEntry>> ReadNewWordSourcesAsync(Guid studentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Card keys of the student's pending items after a best-effort sync: these cards are not scheduled for review
    /// until their submission is approved.
    /// </summary>
    Task<IReadOnlySet<CardKey>> ReadPausedCardKeysAsync(Guid studentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The catalog location for a card whose questions so far came from a meaning submission (book ID
    /// VocabNotebook.SubmissionBookId), or null while the card gets no questions. A ready item with the card's key gives
    /// its location; a pending or rejected item gives null. Without an item for the key (the item was removed, or an
    /// approval changed the key), Lexarbor is asked about the submission: an approval that kept the card's key gives
    /// the approved location, anything else null. Lexarbor unavailable throws VocabCatalogUnavailableException.
    /// </summary>
    Task<VocabularyPlanScopeEntry?> ResolveSubmissionCardAsync(
        Guid studentId, CardKey key, string submissionId, CancellationToken cancellationToken = default);
}
