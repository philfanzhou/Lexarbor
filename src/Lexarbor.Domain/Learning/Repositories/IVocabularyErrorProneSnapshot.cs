using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabularyErrorProneSnapshot : IAsyncDisposable
{
    Task<IReadOnlyDictionary<CardKey, VocabularyErrorProneDisplay>> ReadSnapshotsAsync(IReadOnlyList<CardKey> keys,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    bool StudentExists { get; }
    Task<VocabularyHistoryCoverage> ReadCoverageAsync(DateTimeOffset now, DateTimeOffset windowStart, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VocabularyErrorProneRow>> ReadCardsAsync(DateTimeOffset now, DateTimeOffset windowStart, CancellationToken cancellationToken = default);
}
