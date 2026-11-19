using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;
public interface IMemoryReviewLogRepository
{
    Task AddAsync(MemoryReviewLogModel log, CancellationToken cancellationToken = default);
}
