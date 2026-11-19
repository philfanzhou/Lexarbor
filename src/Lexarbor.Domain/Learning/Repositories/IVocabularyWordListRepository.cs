namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabularyWordListRepository
{
    Task<IVocabularyWordListSnapshot> OpenAsync(Guid studentId, CancellationToken cancellationToken = default);
}
