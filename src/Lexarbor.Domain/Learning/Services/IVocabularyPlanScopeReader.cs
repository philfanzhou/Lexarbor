using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

public sealed record VocabularyPlanScope(int WordCount);
public interface IVocabularyPlanScopeReader
{
    Task<VocabularyPlanScope> ReadScopeAsync(IReadOnlyList<VocabPlanSegment> segments, CancellationToken cancellationToken = default);
}
