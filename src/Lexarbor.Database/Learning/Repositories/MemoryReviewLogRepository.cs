using Lexarbor.Learning.Database.Entities;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

namespace Lexarbor.Learning.Database.Repositories;

public class MemoryReviewLogRepository : IMemoryReviewLogRepository
{
    private readonly LearningDbContext _dbContext;

    public MemoryReviewLogRepository(LearningDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task AddAsync(MemoryReviewLogModel log, CancellationToken cancellationToken = default)
    {
        _dbContext.MemoryReviewLogs.Add(new MemoryReviewLogEntity
        {
            StudentId = log.StudentId,
            WordKey = log.WordKey,
            MeaningKey = log.MeaningKey,
            Rating = (int)log.Rating,
            ReviewedAt = log.ReviewedAt,
            StateBefore = (int?)log.StateBefore,
            StateAfter = (int?)log.StateAfter,
            StabilityBefore = log.StabilityBefore,
            DifficultyBefore = log.DifficultyBefore,
            DueBefore = log.DueBefore,
            StabilityAfter = log.StabilityAfter,
            DifficultyAfter = log.DifficultyAfter,
            DueAfter = log.DueAfter,
            ElapsedDays = log.ElapsedDays,
        });
        return Task.CompletedTask;
    }
}
