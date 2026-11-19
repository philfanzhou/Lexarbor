namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabularyOverviewRepository
{
    Task<IVocabularyOverviewSnapshot> OpenAsync(Guid studentId, CancellationToken cancellationToken = default);
}
