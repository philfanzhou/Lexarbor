using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IVocabularyRelationRepository
{
    Task<bool> WordExistsAsync(string id, CancellationToken cancellationToken);
    Task<VocabularyRelationModel?> GetAsync(string id, CancellationToken cancellationToken);
    Task<VocabularyRelationModel?> FindAsync(string sourceWordId, string targetWordId, string type, CancellationToken cancellationToken);
    Task<VocabularyRelationPage> ListAsync(string? wordId, VocabularyRelationQuery query, CancellationToken cancellationToken);
    Task AddAsync(VocabularyRelationModel relation, CancellationToken cancellationToken);
    Task<bool> ReplaceAsync(VocabularyRelationModel relation, long expectedVersion, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string id, long expectedVersion, CancellationToken cancellationToken);
}
