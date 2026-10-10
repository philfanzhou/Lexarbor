using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class VocabularyMeaningEditService(IVocabularyRepository words, IVocabularyBookRepository books,
    IVocabularyMeaningRepository meanings, IVocabularyMeaningUnitRepository positions, IUnitOfWork unitOfWork)
{
    public async Task ReplaceAsync(string bookId, string wordId, string meaningId, string? partOfSpeech,
        string? meaning, string? example, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(meaning)) throw new DomainValidationException("Meaning is required.");
        var normalizedMeaning = meaning.Trim();
        var normalizedPartOfSpeech = partOfSpeech?.Trim().ToLowerInvariant() ?? string.Empty;
        cancellationToken.ThrowIfCancellationRequested();
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            _ = await books.GetByIdAsync(bookId) ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
            _ = await words.GetByIdAsync(wordId) ?? throw new ResourceNotFoundException("Vocabulary word was not found.");
            var current = await meanings.GetByIdAsync(meaningId)
                ?? throw new ResourceNotFoundException("Vocabulary meaning was not found.");
            if (current.BookId != bookId || current.VocabularyId != wordId)
                throw new ConflictException("Vocabulary meaning does not belong to the requested book and word.");
            // A phrase carries no part of speech. An unchanged value is kept so
            // that existing data stays editable; only a new one is rejected.
            if (normalizedPartOfSpeech.Length > 0 &&
                !string.Equals(normalizedPartOfSpeech, current.PartOfSpeech, StringComparison.Ordinal) &&
                (await positions.GetByMeaningIdAsync(meaningId)).Any(position =>
                    position.EntryKind == "phrase"))
                throw new DomainValidationException("Meanings with a phrase position must not include partOfSpeech.");
            var equivalent = await meanings.GetEquivalentAsync(wordId, bookId, normalizedPartOfSpeech, normalizedMeaning);
            if (equivalent != null && equivalent.Id != meaningId)
                throw new ConflictException("An equivalent vocabulary meaning already exists.");
            current.PartOfSpeech = normalizedPartOfSpeech;
            current.Meaning = normalizedMeaning;
            current.Example = string.IsNullOrWhiteSpace(example) ? null : example.Trim();
            current.UpdatedAt = DateTimeOffset.UtcNow;
            await meanings.UpdateAsync(current);
            return await unitOfWork.SaveChangesAsync();
        });
    }
}
