namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabularyPlanStatsRepository
{
    Task<IVocabularyPlanStatsSnapshot> OpenAsync(IReadOnlyList<Guid> planIds, CancellationToken cancellationToken = default);
}
