using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

// One read-only repeatable-read snapshot; disposing rolls it back.
public interface IVocabularyWordListSnapshot : IAsyncDisposable
{
    bool StudentExists { get; }
    Task<IReadOnlyList<VocabularyWordListCard>> ReadCardsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<CardKey, VocabularyErrorProneDisplay>> ReadSnapshotsAsync(IReadOnlyList<CardKey> keys,
        DateTimeOffset now, CancellationToken cancellationToken = default);
}
