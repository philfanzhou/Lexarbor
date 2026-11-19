using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

public interface IVocabularyFamilyCatalog
{
    Task<IReadOnlyList<VocabularyFamilyRelation>> GetFamilyAsync(string wordId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, IReadOnlyList<VocabularyFamilyRelation>>> LookupFamiliesAsync(
        IReadOnlyList<string> wordIds, CancellationToken cancellationToken);
}
