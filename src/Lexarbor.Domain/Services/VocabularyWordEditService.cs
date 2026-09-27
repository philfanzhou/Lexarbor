using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class VocabularyWordEditService(IVocabularyRepository words,
    IVocabularyWordEditRepository edits, IUnitOfWork unitOfWork)
{
    public async Task ReplaceAsync(string wordId, string? word, string? phoneticUk, string? phoneticUs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(word)) throw new DomainValidationException("Word is required.");
        var normalized = word.Trim().ToLowerInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        // Once admitted, finish the existing non-cancellable transaction. A lost
        // HTTP response cannot promise that a successful commit was undone.
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var current = await edits.GetCurrentAsync(wordId)
                ?? throw new ResourceNotFoundException("Vocabulary word was not found.");
            if (await edits.HasOtherNormalizedWordAsync(normalized, wordId))
                throw new ConflictException("A vocabulary word with the same normalized value already exists.");
            current.Word = normalized;
            current.PhoneticUk = Optional(phoneticUk);
            current.PhoneticUs = Optional(phoneticUs);
            current.UpdatedAt = DateTimeOffset.UtcNow;
            await words.UpdateAsync(current);
            return await unitOfWork.SaveChangesAsync();
        });
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
