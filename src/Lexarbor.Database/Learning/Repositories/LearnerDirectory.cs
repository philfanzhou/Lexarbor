using Lexarbor.Learning.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Learning.Database.Repositories;

public sealed class LearnerDirectory(LearningDbContext context) : ILearnerDirectory
{
    public Task<bool> ExistsAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        context.Learners.AnyAsync(learner => learner.Id == studentId, cancellationToken);
}
