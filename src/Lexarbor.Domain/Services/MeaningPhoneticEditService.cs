using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class MeaningPhoneticEditService(IMeaningPhoneticRepository phonetics, IUnitOfWork unitOfWork)
{
    // Can also join a larger edit transaction on the same scoped UnitOfWork.
    // Persisted work is sufficient for recovery; notifications are only hints.
    public Task<IReadOnlyList<MeaningPhoneticEditResult>> ApplyAsync(IReadOnlyList<MeaningPhoneticEdit> edits,
        MeaningPhoneticWorkVersions configuration, long now, CancellationToken token = default)
    {
        if (edits.Count is < 1 or > 2 || edits.Select(edit => (edit.WordId, edit.MeaningId, edit.Locale)).Distinct().Count() != edits.Count
            || edits.Any(edit => edit.BookId != edits[0].BookId || edit.WordId != edits[0].WordId || edit.MeaningId != edits[0].MeaningId))
            throw new ArgumentException("Edit one meaning and at most one value per accent.", nameof(edits));
        return unitOfWork.ExecuteInTransactionAsync<IReadOnlyList<MeaningPhoneticEditResult>>(async () =>
        {
            var results = new List<MeaningPhoneticEditResult>();
            foreach (var edit in edits)
                results.Add(await phonetics.UpdateInTransactionAsync(edit, configuration, now, token));
            return results;
        }, token);
    }
}
