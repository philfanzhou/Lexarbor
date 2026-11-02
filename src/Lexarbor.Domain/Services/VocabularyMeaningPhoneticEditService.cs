using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

/// <summary>One writer for a meaning's text and optional specific pronunciations.</summary>
public sealed class VocabularyMeaningPhoneticEditService(VocabularyMeaningEditService meanings,
    MeaningPhoneticEditService phonetics, IVocabularyMeaningRepository repository,
    IVocabularyMeaningUnitRepository positions, IUnitOfWork unitOfWork,
    IVocabularyBookRepository books, IVocabularyRepository words)
{
    public Task ReplaceAsync(string bookId, string wordId, string meaningId, string? partOfSpeech,
        string? meaning, string? example, IReadOnlyList<MeaningPhoneticEdit> edits,
        MeaningPhoneticWorkVersions versions, long now, CancellationToken token = default) =>
        unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            if (edits.Any(edit => edit.BookId != bookId || edit.WordId != wordId || edit.MeaningId != meaningId))
                throw new ArgumentException("Phonetic edits must belong to the requested meaning.", nameof(edits));
            if (edits.Count > 0)
            {
                _ = await books.GetByIdAsync(bookId) ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
                _ = await words.GetByIdAsync(wordId) ?? throw new ResourceNotFoundException("Vocabulary word was not found.");
                var current = await repository.GetByIdAsync(meaningId)
                    ?? throw new ResourceNotFoundException("Vocabulary meaning was not found.");
                if (current.BookId != bookId || current.VocabularyId != wordId)
                    throw new ConflictException("Vocabulary meaning does not belong to the requested book and word.");
                if ((await positions.GetByMeaningIdAsync(meaningId)).Any(position => position.EntryKind == "phrase") &&
                    edits.Any(edit => edit.IsProvided && !string.IsNullOrWhiteSpace(edit.Value) &&
                        AudioSelectionService.NormalizePhonetic(edit.Value) != AudioSelectionService.NormalizePhonetic(
                            edit.Locale == AudioLocale.EnGb ? current.PhoneticUk : current.PhoneticUs)))
                    throw new DomainValidationException("Meanings with a phrase position must not include new specific phonetics.");
                // The source revision includes POS and meaning. Check both old
                // accent snapshots before the legacy text replacement changes it.
                await phonetics.ApplyAsync(edits, versions, now, token);
            }
            await meanings.ReplaceAsync(bookId, wordId, meaningId, partOfSpeech, meaning, example, token);
            return true;
        }, token);
}
