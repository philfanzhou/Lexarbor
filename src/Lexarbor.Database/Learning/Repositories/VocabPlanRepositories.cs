using System.Text.Json;
using Lexarbor.Learning.Database.Entities;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Learning.Database.Repositories;

public class VocabPlanRepository : IVocabPlanRepository
{
    private static readonly JsonSerializerOptions UnitIdsJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly LearningDbContext _dbContext;

    public VocabPlanRepository(LearningDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<VocabPlanModel?> FindByIdAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabPlans.FindAsync(new object[] { planId }, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public virtual Task<VocabPlanModel?> FindActiveByStudentAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        FirstByStudentAsync(studentId, cancellationToken);

    private async Task<VocabPlanModel?> FirstByStudentAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var query = _dbContext.VocabPlans.AsNoTracking().Where(p => p.StudentId == studentId);
        query = query.Where(p => p.Status == (int)VocabPlanStatus.Active);

        var entity = await query
            .OrderByDescending(p => p.UpdatedAt)
            .ThenByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<List<VocabPlanModel>> ListByIdsAsync(IReadOnlyCollection<Guid> planIds, CancellationToken cancellationToken = default)
    {
        if (planIds.Count == 0)
        {
            return [];
        }

        var entities = await _dbContext.VocabPlans.AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToModel).ToList();
    }

    public Task AddAsync(VocabPlanModel plan, CancellationToken cancellationToken = default)
    {
        return _dbContext.VocabPlans.AddAsync(ToEntity(plan), cancellationToken).AsTask();
    }

    public async Task UpdateAsync(VocabPlanModel plan, CancellationToken cancellationToken = default)
    {


        var entity = await _dbContext.VocabPlans.FindAsync(new object[] { plan.Id }, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            throw new InvalidOperationException($"Vocabulary plan {plan.Id} is not loaded and cannot be updated.");
        }

        entity.BookId = plan.BookId;
        entity.UnitIds = SerializeUnitIds(plan.UnitIds);
        entity.Segments = SerializeSegments(plan.Segments);
        entity.DailyNewWords = plan.DailyNewWords;
        entity.Controller = (int)plan.Controller;
        entity.ControllerTeacherId = plan.ControllerTeacherId;
        entity.Status = (int)plan.Status;
        entity.TotalWords = plan.TotalWords;
        entity.UpdatedAt = plan.UpdatedAt;
        entity.Version = plan.Version + 1;
        _dbContext.Entry(entity).Property(e => e.Version).OriginalValue = plan.Version;
        plan.Version = entity.Version;
    }

    internal static string SerializeSegments(IReadOnlyList<VocabPlanSegment> segments) =>
        JsonSerializer.Serialize(segments.Select(segment => new VocabPlanSegment(
            segment.BookId, segment.UnitIds is { Count: > 0 } ? segment.UnitIds : null)), UnitIdsJsonOptions);

    /// <summary>Preserve review-only scopes; retain legacy one-book fallback when a book id exists.</summary>
    internal static List<VocabPlanSegment> DeserializeSegments(string? segments, string bookId, string? unitIds)
    {
        var parsed = string.IsNullOrEmpty(segments) ? null : JsonSerializer.Deserialize<List<VocabPlanSegment>>(segments, UnitIdsJsonOptions);
        if (parsed is null || (parsed.Count == 0 && !string.IsNullOrWhiteSpace(bookId)))
        {
            return string.IsNullOrWhiteSpace(bookId) ? [] : [new VocabPlanSegment(bookId, DeserializeUnitIds(unitIds))];
        }
        return parsed.Select(segment => segment with { UnitIds = segment.UnitIds is { Count: > 0 } ? segment.UnitIds : null }).ToList();
    }

    private static string? SerializeUnitIds(List<string>? unitIds) =>
        unitIds is null || unitIds.Count == 0 ? null : JsonSerializer.Serialize(unitIds, UnitIdsJsonOptions);

    internal static List<string>? DeserializeUnitIds(string? unitIds)
    {
        if (string.IsNullOrEmpty(unitIds))
        {
            return null;
        }
        var parsed = JsonSerializer.Deserialize<List<string>>(unitIds, UnitIdsJsonOptions);
        return parsed is null || parsed.Count == 0 ? null : parsed;
    }

    private static VocabPlanEntity ToEntity(VocabPlanModel model) => new()
    {
        Id = model.Id,
        StudentId = model.StudentId,
        BookId = model.BookId,
        UnitIds = SerializeUnitIds(model.UnitIds),
        Segments = SerializeSegments(model.Segments),
        DailyNewWords = model.DailyNewWords,
        Controller = (int)model.Controller,
        ControllerTeacherId = model.ControllerTeacherId,
        Status = (int)model.Status,
        StartDate = model.StartDate,
        TotalWords = model.TotalWords,
        Version = model.Version,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
    };

    private static VocabPlanModel ToModel(VocabPlanEntity entity) => new()
    {
        Id = entity.Id,
        StudentId = entity.StudentId,
        Segments = DeserializeSegments(entity.Segments, entity.BookId, entity.UnitIds),
        DailyNewWords = entity.DailyNewWords,
        Controller = (VocabPlanController)entity.Controller,
        ControllerTeacherId = entity.ControllerTeacherId,
        Status = (VocabPlanStatus)entity.Status,
        StartDate = entity.StartDate,
        TotalWords = entity.TotalWords,
        Version = entity.Version,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };
}

public class VocabPlanChangeRepository : IVocabPlanChangeRepository
{
    private readonly LearningDbContext _dbContext;

    public VocabPlanChangeRepository(LearningDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task AddAsync(VocabPlanChangeModel change, CancellationToken cancellationToken = default)
    {
        return _dbContext.VocabPlanChanges.AddAsync(new VocabPlanChangeEntity
        {
            Id = change.Id,
            PlanId = change.PlanId,
            ActorType = (int)change.ActorType,
            ActorTeacherId = change.ActorTeacherId,
            Action = change.Action,
            BeforeValues = change.BeforeValues,
            AfterValues = change.AfterValues,
            CreatedAt = change.CreatedAt,
        }, cancellationToken).AsTask();
    }

    public async Task<DateTimeOffset?> FindLatestTeacherUpdateAtAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        // max(created_at) over one plan's rows; served by IX_vocab_plan_change_plan_id_created_at.
        var latest = await _dbContext.VocabPlanChanges.AsNoTracking()
            .Where(c => c.PlanId == planId
                && c.ActorType == (int)VocabPlanActorType.Teacher
                && c.Action == VocabPlanChangeActions.Update)
            .MaxAsync(c => (DateTimeOffset?)c.CreatedAt, cancellationToken).ConfigureAwait(false);
        return latest?.ToUniversalTime();
    }
}
