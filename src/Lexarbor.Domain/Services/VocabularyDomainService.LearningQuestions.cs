using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Services;

public partial class VocabularyDomainService
{
    private readonly LearningQuestionGenerator _learningQuestions;

    public async Task<LearningQuestion> CreateLearningQuestionAsync(
        string wordId, string bookId, string meaningId, LearningQuestionMode mode,
        string? unitId = null, bool sameEntryKind = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(wordId) || string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(meaningId))
            throw new DomainValidationException("Word ID, book ID and meaning ID are required.");
        var (word, meaning, scope) = await ResolveQuestionTargetAsync(
            wordId, bookId, meaningId, unitId, sameEntryKind, cancellationToken);
        return await _learningQuestions.CreateAsync(word.Word, meaning.Meaning, mode,
            (direction, token) => BuildQuestionAsync(word, meaning, scope, direction, token), cancellationToken);
    }
}
