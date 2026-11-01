using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

// Internal edit contract. Absence differs from an explicit null/empty clear.
// Expected conditions describe the target's OWN binding, never its fallback.
public sealed record MeaningPhoneticSnapshot(string? Value, long Epoch, string SourceRevision, string? OwnAssetId);
public sealed record MeaningPhoneticEdit(string BookId, string WordId, string MeaningId, AudioLocale Locale,
    bool IsProvided, string? Value, MeaningPhoneticSnapshot Expected);
public sealed record MeaningPhoneticEditResult(bool Changed, MeaningPhoneticSnapshot Current);

public sealed record MeaningPhoneticWorkVersions(string? Preparation, string? UkAudio, string? UsAudio);

public interface IMeaningPhoneticRepository
{
    Task<MeaningPhoneticSnapshot> ReadAsync(string bookId, string wordId, string meaningId, AudioLocale locale,
        CancellationToken token);
    // Joins the caller's actual UnitOfWork writer/context; does not commit or do IO.
    Task<MeaningPhoneticEditResult> UpdateInTransactionAsync(MeaningPhoneticEdit edit,
        MeaningPhoneticWorkVersions configuration, long now, CancellationToken token);
}
