using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

// All values are materialized from one read-only repeatable-read snapshot; no connection is retained.
public interface IVocabularyOverviewSnapshot : IAsyncDisposable
{
    bool StudentExists { get; }
    VocabPlanModel? ActivePlan { get; }
    Task<IReadOnlyList<MemoryCardModel>> ReadCardsAsync(CancellationToken cancellationToken = default);
}
