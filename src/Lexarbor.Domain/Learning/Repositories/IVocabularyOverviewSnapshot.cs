using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

// All values are materialized from one read-only repeatable-read snapshot; no connection is retained.
public interface IVocabularyOverviewSnapshot : IAsyncDisposable
{
    bool StudentExists { get; }
    VocabPlan? ActivePlan { get; }
    Task<IReadOnlyList<MemoryCard>> ReadCardsAsync(CancellationToken cancellationToken = default);
}
