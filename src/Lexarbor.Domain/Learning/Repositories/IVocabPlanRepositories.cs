namespace Lexarbor.Learning.Domain.Repositories;

using Lexarbor.Learning.Domain.Models;

public interface IVocabPlanRepository
{
    Task<VocabPlan?> FindByIdAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<VocabPlan?> FindActiveByStudentAsync(Guid studentId, CancellationToken cancellationToken = default);

    Task<List<VocabPlan>> ListByIdsAsync(IReadOnlyCollection<Guid> planIds, CancellationToken cancellationToken = default);

    Task AddAsync(VocabPlan plan, CancellationToken cancellationToken = default);
    Task UpdateAsync(VocabPlan plan, CancellationToken cancellationToken = default);
}

public interface IVocabPlanChangeRepository
{
    Task AddAsync(VocabPlanChange change, CancellationToken cancellationToken = default);
    Task<DateTimeOffset?> FindTeacherAdjustedAtAsync(Guid planId, CancellationToken cancellationToken = default);
}
