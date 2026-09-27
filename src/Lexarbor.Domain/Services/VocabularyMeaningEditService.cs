using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class VocabularyMeaningEditService(IVocabularyRepository words, IVocabularyBookRepository books,
    IVocabularyMeaningRepository meanings, IUnitOfWork unitOfWork)
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
