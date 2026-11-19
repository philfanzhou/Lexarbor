namespace Lexarbor.Learning.Domain.Repositories;

public interface ILearnerDirectory
{
    Task<bool> ExistsAsync(Guid studentId, CancellationToken cancellationToken = default);
}
