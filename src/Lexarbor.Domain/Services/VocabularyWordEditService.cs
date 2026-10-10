using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class VocabularyWordEditService(IVocabularyRepository words,
    IVocabularyWordEditRepository edits, IVocabularyMeaningRepository meanings,
    IVocabularyMeaningUnitRepository positions, IUnitOfWork unitOfWork)
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
            // The submitted spelling is the display value: an explicit edit is
            // the one path allowed to correct casing, because it is confined to
            // the row whose normalized key is unchanged. Imports never rewrite
            // display spelling; equivalence stays keyed on the normalized form.
            var newPhoneticUk = Optional(phoneticUk);
            var newPhoneticUs = Optional(phoneticUs);
            var addsUk = IsNewValue(newPhoneticUk, current.PhoneticUk);
            var addsUs = IsNewValue(newPhoneticUs, current.PhoneticUs);
            if ((addsUk || addsUs) && await IsUsedOnlyAsPhraseAsync(wordId))
                throw new DomainValidationException(
                    $"Words used only as phrases must not include {(addsUk ? "phoneticUk" : "phoneticUs")}.");
            current.Word = word.Trim();
            current.PhoneticUk = newPhoneticUk;
            current.PhoneticUs = newPhoneticUs;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            await words.UpdateAsync(current);
            return await unitOfWork.SaveChangesAsync();
        });
    }

    /// <summary>
    /// A phrase carries no phonetics. Phonetics live on the spelling every
    /// position shares, so they are refused only when every position of the
    /// spelling is a phrase; a spelling with no position at all, or with a
    /// word or unclassified position, keeps them.
    /// </summary>
    private async Task<bool> IsUsedOnlyAsPhraseAsync(string wordId)
    {
        var any = false;
        foreach (var meaning in await meanings.GetByVocabularyIdAsync(wordId))
        {
            foreach (var position in await positions.GetByMeaningIdAsync(meaning.Id))
            {
                if (position.EntryKind != "phrase") return false;
                any = true;
            }
        }

        return any;
    }

    // An unchanged value is kept so that existing data stays editable.
    private static bool IsNewValue(string? requested, string? current) =>
        requested != null && !string.Equals(requested, current, StringComparison.Ordinal);

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
