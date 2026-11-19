namespace Lexarbor.Learning.Domain.Repositories;

using Lexarbor.Learning.Domain.Models;

public interface IVocabPlanRepository
{
    Task<VocabPlanModel?> FindByIdAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<VocabPlanModel?> FindActiveByStudentAsync(Guid studentId, CancellationToken cancellationToken = default);

    Task<List<VocabPlanModel>> ListByIdsAsync(IReadOnlyCollection<Guid> planIds, CancellationToken cancellationToken = default);

    Task AddAsync(VocabPlanModel plan, CancellationToken cancellationToken = default);
    Task UpdateAsync(VocabPlanModel plan, CancellationToken cancellationToken = default);
}

public interface IVocabPlanChangeRepository
{

    Task AddAsync(VocabPlanChangeModel change, CancellationToken cancellationToken = default);
    Task<DateTimeOffset?> FindLatestTeacherUpdateAtAsync(Guid planId, CancellationToken cancellationToken = default);
}
