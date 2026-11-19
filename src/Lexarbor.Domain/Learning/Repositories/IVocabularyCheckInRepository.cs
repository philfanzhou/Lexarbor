using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabularyCheckInRepository
{
    /// <summary>Reads every plan of the student and the local dates of its completed plan days in one read-only snapshot.</summary>
    Task<VocabularyCheckInHistory> ReadAsync(Guid studentId, CancellationToken cancellationToken = default);
}
