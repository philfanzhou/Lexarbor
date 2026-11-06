using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IVocabularyExpansionRepository
{
    Task<VocabularyExpansionInput?> ReadAsync(EnrichmentTargetKey target, CancellationToken token);
    Task MarkGeneratedAsync(string wordId, string taskId, CancellationToken token);
}
