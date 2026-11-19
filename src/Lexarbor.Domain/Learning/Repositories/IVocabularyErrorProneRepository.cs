namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabularyErrorProneRepository
{
    Task<IVocabularyErrorProneSnapshot> OpenAsync(Guid studentId, CancellationToken cancellationToken = default);
}
