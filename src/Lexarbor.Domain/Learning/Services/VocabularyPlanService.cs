using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

namespace Lexarbor.Learning.Domain.Services;

/// <summary>
/// Manages continuous settings with one Active routine per student. Versioned changes are
/// recorded once; teacher adjustments transfer control and students may only expand a
/// teacher-controlled scope or raise its per-group size. Catalog failures precede writes.
/// </summary>
public sealed class VocabularyPlanService
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IVocabPlanRepository _planRepository;
    private readonly IVocabPlanChangeRepository _changeRepository;
    private readonly IVocabPlanDayRepository _dayRepository;
    private readonly IVocabularyPlanScopeReader _scopeReader;
    private readonly IUnitOfWork _unitOfWork;

    public VocabularyPlanService(
        IVocabPlanRepository planRepository,
        IVocabPlanChangeRepository changeRepository,
        IVocabPlanDayRepository dayRepository,
        IVocabularyPlanScopeReader scopeReader,
        IUnitOfWork unitOfWork)
    {
        _planRepository = planRepository;
        _changeRepository = changeRepository;
        _dayRepository = dayRepository;
        _scopeReader = scopeReader;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Read the current continuous routine without loading retired settings.</summary>
    public Task<VocabPlanModel?> GetStudentPlanAsync(
        Guid studentId, CancellationToken cancellationToken = default) =>
        _planRepository.FindActiveByStudentAsync(studentId, cancellationToken);
    public Task<DateTimeOffset?> GetTeacherAdjustedAtAsync(Guid planId, CancellationToken cancellationToken = default) =>
        _changeRepository.FindLatestTeacherUpdateAtAsync(planId, cancellationToken);

    public Task<List<VocabPlanModel>> ListByIdsAsync(IReadOnlyCollection<Guid> planIds, CancellationToken cancellationToken = default) =>
        _planRepository.ListByIdsAsync(planIds, cancellationToken);

    public Task<VocabPlanModel> CreateAsync(
        Guid studentId,
        VocabPlanActor actor,
        string bookId,
        IReadOnlyList<string>? unitIds,
        int dailyNewWords,
        CancellationToken cancellationToken = default) =>
        CreateAsync(studentId, actor, [new VocabPlanSegment(bookId, unitIds?.ToList())], dailyNewWords, cancellationToken);
    public async Task<VocabPlanModel> CreateAsync(
        Guid studentId,
        VocabPlanActor actor,
        IReadOnlyList<VocabPlanSegment> segments,
        int dailyNewWords,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSegments(segments);
        if (await _planRepository.FindActiveByStudentAsync(studentId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new VocabPlanActiveExistsException();
        }

        var scope = await _scopeReader.ReadScopeAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (scope.WordCount <= 0)
        {
            throw new VocabPlanScopeEmptyException();
        }

        var now = DateTimeOffset.UtcNow;
        var plan = new VocabPlanModel
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            Segments = normalized,
            DailyNewWords = dailyNewWords,
            Controller = VocabPlanController.Student,
            ControllerTeacherId = null,
            Status = VocabPlanStatus.Active,
            StartDate = Lexarbor.Learning.Domain.Calendar.LearningDay.From(now),
            TotalWords = scope.WordCount,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (actor.Type == VocabPlanActorType.Teacher)
        {
            plan.Controller = VocabPlanController.Teacher;
            plan.ControllerTeacherId = actor.TeacherId;
        }

        await _planRepository.AddAsync(plan, cancellationToken).ConfigureAwait(false);
        await _changeRepository.AddAsync(new VocabPlanChangeModel
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            ActorType = actor.Type,
            ActorTeacherId = actor.TeacherId,
            Action = VocabPlanChangeActions.Create,
            BeforeValues = null,
            AfterValues = SerializeSnapshot(plan),
            CreatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return plan;
    }
    public async Task<VocabPlanModel> UpdateAsync(
        Guid planId,
        VocabPlanActor actor,
        int expectedVersion,
        string? bookId,
        IReadOnlyList<string>? unitIds,
        int? dailyNewWords,
        CancellationToken cancellationToken = default)
    {
        var plan = await RequireActivePlanAsync(planId, cancellationToken).ConfigureAwait(false);
        List<VocabPlanSegment>? segments = null;
        if (bookId is not null || unitIds is not null)
        {
            var first = plan.Segments.FirstOrDefault() ?? new VocabPlanSegment(string.Empty, null);
            segments = [new VocabPlanSegment(bookId ?? first.BookId, unitIds is null ? first.UnitIds : unitIds.ToList()), .. plan.Segments.Skip(1)];
        }
        return await UpdateAsync(plan, actor, expectedVersion, segments, dailyNewWords, cancellationToken).ConfigureAwait(false);
    }
    public async Task<VocabPlanModel> UpdateAsync(
        Guid planId,
        VocabPlanActor actor,
        int expectedVersion,
        IReadOnlyList<VocabPlanSegment>? segments,
        int? dailyNewWords,
        CancellationToken cancellationToken = default)
    {
        var plan = await RequireActivePlanAsync(planId, cancellationToken).ConfigureAwait(false);
        return await UpdateAsync(plan, actor, expectedVersion, segments, dailyNewWords, cancellationToken).ConfigureAwait(false);
    }

    private async Task<VocabPlanModel> UpdateAsync(
        VocabPlanModel plan,
        VocabPlanActor actor,
        int expectedVersion,
        IReadOnlyList<VocabPlanSegment>? segments,
        int? dailyNewWords,
        CancellationToken cancellationToken)
    {
        var normalized = segments is null ? null : NormalizeSegments(segments);
        EnsureUpdateAllowed(actor, plan, normalized, dailyNewWords);
        EnsureVersion(plan, expectedVersion);

        var scopeChanged = normalized is not null
            && (normalized.Count != plan.Segments.Count || normalized.Where((segment, index) => !segment.SameAs(plan.Segments[index])).Any());
        var before = SerializeSnapshot(plan);

        if (normalized is not null)
        {
            plan.Segments = normalized;
        }
        if (dailyNewWords.HasValue)
        {
            plan.DailyNewWords = dailyNewWords.Value;
        }
        if (actor.Type == VocabPlanActorType.Teacher && plan.Controller == VocabPlanController.Student)
        {
            // A teacher adjustment transfers control to that teacher; no homework row is created here.
            plan.Controller = VocabPlanController.Teacher;
            plan.ControllerTeacherId = actor.TeacherId;
        }

        if (normalized is not null)
        {
            var scope = await _scopeReader.ReadScopeAsync(plan.Segments, cancellationToken).ConfigureAwait(false);
            if (scope.WordCount <= 0)
            {

                throw new VocabPlanScopeEmptyException();
            }
            plan.TotalWords = scope.WordCount;
        }
        if (scopeChanged)
        {
            await _dayRepository.ClearNewWordsExhaustedAsync(plan.Id, cancellationToken).ConfigureAwait(false);
        }

        var after = SerializeSnapshot(plan);
        await ApplyChangeAsync(plan, actor, VocabPlanChangeActions.Update, before, after, cancellationToken).ConfigureAwait(false);
        return plan;
    }
    public async Task<VocabPlanModel> AdoptAsync(
        Guid planId, Guid teacherId, int expectedVersion, CancellationToken cancellationToken = default)
    {
        var plan = await RequireActivePlanAsync(planId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(plan, expectedVersion);

        var before = SerializeSnapshot(plan);
        plan.Controller = VocabPlanController.Teacher;
        plan.ControllerTeacherId = teacherId;

        await ApplyChangeAsync(plan, VocabPlanActor.AsTeacher(teacherId), VocabPlanChangeActions.Adopt, before, SerializeSnapshot(plan), cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async Task<VocabPlanModel> ReleaseAsync(
        Guid planId, Guid teacherId, int expectedVersion, CancellationToken cancellationToken = default)
    {
        var plan = await RequireActivePlanAsync(planId, cancellationToken).ConfigureAwait(false);
        if (plan.Controller == VocabPlanController.Student)
        {
            EnsureVersion(plan, expectedVersion);
            return plan;
        }
        EnsureVersion(plan, expectedVersion);

        var before = SerializeSnapshot(plan);
        plan.Controller = VocabPlanController.Student;
        plan.ControllerTeacherId = null;

        await ApplyChangeAsync(plan, VocabPlanActor.AsTeacher(teacherId), VocabPlanChangeActions.Release, before, SerializeSnapshot(plan), cancellationToken).ConfigureAwait(false);
        return plan;
    }

    public async Task<bool> RefreshTotalWordsAsync(
        Guid planId, int expectedVersion, int totalWords, CancellationToken cancellationToken = default)
    {
        var plan = await _planRepository.FindByIdAsync(planId, cancellationToken).ConfigureAwait(false);
        if (plan is null || plan.Status != VocabPlanStatus.Active || plan.Version != expectedVersion || plan.TotalWords == totalWords)
        {
            return false;
        }

        plan.TotalWords = totalWords;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await _planRepository.UpdateAsync(plan, cancellationToken).ConfigureAwait(false);
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (VocabPlanVersionConflictException)
        {
            // A concurrent edit already rewrote the scope snapshot; keep it.
            _unitOfWork.DiscardChanges();
            return false;
        }
    }

    private async Task<VocabPlanModel> RequireActivePlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await _planRepository.FindByIdAsync(planId, cancellationToken).ConfigureAwait(false)
            ?? throw new VocabPlanNotFoundException(planId);
        if (plan.Status != VocabPlanStatus.Active)
        {
            throw new VocabPlanNotActiveException();
        }
        return plan;
    }

    private static void EnsureVersion(VocabPlanModel plan, int expectedVersion)
    {
        if (plan.Version != expectedVersion)
        {
            throw new VocabPlanVersionConflictException();
        }
    }

    private static void EnsureUpdateAllowed(
        VocabPlanActor actor, VocabPlanModel plan, IReadOnlyList<VocabPlanSegment>? segments, int? dailyNewWords)
    {
        // Teacher actor: may modify any active plan; control transfer is applied by the caller.
        if (actor.Type == VocabPlanActorType.Teacher)
        {
            return;
        }

        // Student actor: full rights on a student-controlled plan.
        if (plan.Controller == VocabPlanController.Student)
        {
            return;
        }

        // Student on a teacher-controlled plan may only expand the scope or raise dailyNewWords (ADR-0019).
        if (segments is not null && !IsExpansion(plan.Segments, segments))
        {
            throw new VocabPlanForbiddenException("A student can only expand the scope of a teacher-controlled plan.");
        }
        if (dailyNewWords.HasValue && dailyNewWords.Value < plan.DailyNewWords)
        {
            throw new VocabPlanForbiddenException("A student can only raise the daily new-word count of a teacher-controlled plan.");
        }
    }

    private static bool IsExpansion(IReadOnlyList<VocabPlanSegment> current, IReadOnlyList<VocabPlanSegment> next) =>
        next.Count >= current.Count && current.Select((segment, index) => next[index].Covers(segment)).All(covered => covered);
    private static List<VocabPlanSegment> NormalizeSegments(IReadOnlyList<VocabPlanSegment> segments)
    {
        if (segments.Count is < 1 or > 20)
        {
            throw new VocabPlanScopeInvalidException("The vocabulary plan scope needs between one and twenty books.");
        }

        var normalized = new List<VocabPlanSegment>(segments.Count);
        var books = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            var bookId = segment.BookId?.Trim();
            if (string.IsNullOrEmpty(bookId))
            {
                throw new VocabPlanScopeInvalidException("Each vocabulary plan book needs a book id.");
            }
            if (!books.Add(bookId))
            {
                throw new VocabPlanScopeInvalidException("A book can appear only once in a vocabulary plan scope.");
            }

            List<string>? unitIds = null;
            if (segment.UnitIds is { Count: > 0 })
            {
                var trimmed = segment.UnitIds.Select(unitId => (unitId ?? string.Empty).Trim()).ToList();
                if (trimmed.Any(string.IsNullOrEmpty))
                {
                    throw new VocabPlanScopeInvalidException("Unit ids must not be empty.");
                }
                unitIds = trimmed.Distinct(StringComparer.Ordinal).ToList();
            }
            normalized.Add(new VocabPlanSegment(bookId, unitIds));
        }

        return normalized;
    }

    private async Task ApplyChangeAsync(
        VocabPlanModel plan, VocabPlanActor actor, string action, string? before, string? after, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        plan.UpdatedAt = now;
        await _planRepository.UpdateAsync(plan, cancellationToken).ConfigureAwait(false);
        await _changeRepository.AddAsync(new VocabPlanChangeModel
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            ActorType = actor.Type,
            ActorTeacherId = actor.TeacherId,
            Action = action,
            BeforeValues = before,
            AfterValues = after,
            CreatedAt = now,
        }, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? SerializeSnapshot(VocabPlanModel plan) => JsonSerializer.Serialize(new
    {
        plan.BookId,
        plan.UnitIds,
        plan.Segments,
        plan.DailyNewWords,
        controller = plan.Controller.ToString(),
        plan.ControllerTeacherId,
        status = plan.Status.ToString(),
        plan.TotalWords,
    }, SnapshotJsonOptions);
}
