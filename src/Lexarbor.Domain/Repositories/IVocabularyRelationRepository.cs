using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IVocabularyRelationRepository
{
    Task<bool> WordExistsAsync(string id, CancellationToken cancellationToken);
    Task<VocabularyRelation?> GetAsync(string id, CancellationToken cancellationToken);
    Task<VocabularyRelation?> FindAsync(string sourceWordId, string targetWordId, string type, CancellationToken cancellationToken);
    Task<VocabularyRelationPage> ListAsync(string? wordId, VocabularyRelationQuery query, CancellationToken cancellationToken);
    Task AddAsync(VocabularyRelation relation, CancellationToken cancellationToken);
    Task<bool> ReplaceAsync(VocabularyRelation relation, long expectedVersion, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string id, long expectedVersion, CancellationToken cancellationToken);
    Task<IReadOnlyList<VocabularyRelationMeaningPair>> GetMeaningPairsAsync(string relationId, CancellationToken cancellationToken);
    Task ReplaceMeaningPairsAsync(VocabularyRelation relation, IReadOnlyList<VocabularyRelationMeaningPair> pairs,
        CancellationToken cancellationToken);
    Task<bool> AddMeaningPairAsync(string relationId, VocabularyRelationMeaningPair pair, CancellationToken cancellationToken);
}
