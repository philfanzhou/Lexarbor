using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class VocabularyCleanupService(IVocabularyCleanupRepository repository, IUnitOfWork unitOfWork)
{
    public Task<VocabularyCleanupPreview> PreviewAsync(string bookId, VocabularyCleanupSelection selection,
        CancellationToken cancellationToken = default)
    {
        selection = Validate(selection, false);
        return repository.ReadSnapshotAsync(async () =>
        {
            var book = await ResolveAsync(bookId, selection, cancellationToken);
            var counts = await repository.CountAsync(bookId, selection, cancellationToken);
            return new VocabularyCleanupPreview(bookId, book.BookName, selection.Action,
                counts.AffectedWordCount, counts.MeaningCount, counts.OrphanWordCount);
        }, cancellationToken);
    }

    public Task<VocabularyCleanupResult> CommitAsync(string bookId, VocabularyCleanupSelection selection,
        CancellationToken cancellationToken = default)
    {
        selection = Validate(selection, true);
        cancellationToken.ThrowIfCancellationRequested();
        return unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            // All state checks occur after the write lock and transaction. Once
            // admitted, request cancellation cannot leave a partial cleanup.
            var book = await ResolveAsync(bookId, selection, CancellationToken.None);
            if (selection.Action == "delete" && !string.Equals(book.BookName, selection.ConfirmedBookName, StringComparison.Ordinal))
                throw new ConflictException("The vocabulary book name confirmation does not match.");
            return await repository.DeleteAsync(bookId, selection);
        });
    }

    private async Task<VocabularyBookModel> ResolveAsync(string bookId, VocabularyCleanupSelection selection, CancellationToken cancellationToken)
    {
        var book = await repository.GetBookAsync(bookId, cancellationToken)
            ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
        if (selection.Action == "removeMeaning")
        {
            if (!await repository.WordExistsAsync(selection.WordId!, cancellationToken))
                throw new ResourceNotFoundException("Vocabulary word was not found.");
            var meaning = await repository.GetMeaningAsync(selection.MeaningId!, cancellationToken)
                ?? throw new ResourceNotFoundException("Vocabulary meaning was not found.");
            if (meaning.BookId != bookId || meaning.VocabularyId != selection.WordId)
                throw new ConflictException("Vocabulary meaning does not belong to the requested book and word.");
        }
        else if (selection.Action == "removeWords" &&
                 await repository.CountSelectedWordsAsync(bookId, selection.WordIds!, cancellationToken) != selection.WordIds!.Count)
            throw new ConflictException("Every selected word must still belong to this vocabulary book.");
        return book;
    }

    private static VocabularyCleanupSelection Validate(VocabularyCleanupSelection selection, bool commit)
    {
        var valid = selection.Action switch
        {
            "removeMeaning" => !string.IsNullOrWhiteSpace(selection.WordId) && !string.IsNullOrWhiteSpace(selection.MeaningId)
                && selection.WordIds == null && selection.ConfirmedBookName == null,
            "removeWords" => selection.WordId == null && selection.MeaningId == null && selection.ConfirmedBookName == null
                && selection.WordIds is { Count: > 0 and <= 100 } && selection.WordIds.All(id => !string.IsNullOrWhiteSpace(id)),
            "clear" => selection.WordId == null && selection.MeaningId == null && selection.WordIds == null && selection.ConfirmedBookName == null,
            "delete" => selection.WordId == null && selection.MeaningId == null && selection.WordIds == null
                && (!commit || selection.ConfirmedBookName != null),
            _ => false
        };
        if (!valid) throw new DomainValidationException("The cleanup request is invalid.");
        return selection with { WordIds = selection.WordIds?.Distinct(StringComparer.Ordinal).ToArray() };
    }
}
