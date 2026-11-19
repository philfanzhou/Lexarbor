using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabularyPlanStatsSnapshot : IAsyncDisposable
{
    IReadOnlyList<VocabPlanModel> Plans { get; }

    IReadOnlyDictionary<Guid, VocabularyPlanReactivation> Reactivations { get; }
    Task<IReadOnlyDictionary<Guid, VocabularyPlanStatsCounts>> ReadCountsAsync(
        IReadOnlyList<VocabularyPlanStatsScope> scopes, CancellationToken cancellationToken = default);
}
