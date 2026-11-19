using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using static Lexarbor.Learning.Service.VocabularyCatalogErrorCodes;

namespace Lexarbor.Learning.Service;

public static class VocabularyPlanHttpEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyPlanHttpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vocabulary");

        group.MapGet("/catalog/books", GetCatalogBooks);
        group.MapGet("/catalog/books/{bookId}/units", GetCatalogBookUnits);

        group.MapGet("/students/{studentId:guid}/plan", GetStudentPlan);
        group.MapPost("/students/{studentId:guid}/plans", CreatePlan);
        group.MapPut("/plans/{planId:guid}", UpdatePlan);
        group.MapPost("/plans/{planId:guid}/adopt", AdoptPlan);
        group.MapPost("/plans/{planId:guid}/release", ReleasePlan);
        group.MapPost("/plans/{planId:guid}/end", EndPlan);
        group.MapPost("/plans/batch-get", BatchGetPlans);

        return app;
    }


    private static async Task<IResult> GetCatalogBooks(ILexarborClient lexarbor, CancellationToken cancellationToken)
    {
        try
        {
            var books = await lexarbor.GetBooksAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = new { books } });
        }
        catch (Exception error)
        {
            return MapDomainError(error);
        }
    }

    private static async Task<IResult> GetCatalogBookUnits(
        string bookId,
        ILexarborClient lexarbor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            return Error(400, "VOCAB_BOOK_ID_REQUIRED", "Book id is required.");
        }

        try
        {
            var units = await lexarbor.GetUnitsAsync(bookId.Trim(), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = new { units } });
        }
        catch (Exception error)
        {
            return MapDomainError(error);
        }
    }


    private static async Task<IResult> GetStudentPlan(
        Guid studentId,
        VocabularyPlanService planService,
        CancellationToken cancellationToken)
    {
        var active = await planService.GetStudentPlanAsync(studentId, cancellationToken).ConfigureAwait(false);
        var teacherAdjustedAt = active is null
            ? null
            : await planService.GetTeacherAdjustedAtAsync(active.Id, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            success = true,
            data = new
            {
                active = ToActiveDto(active, teacherAdjustedAt),
                // Null keeps cached clients readable without querying or exposing retired settings.
                latestFinished = (object?)null,
            },
        });
    }

    private static async Task<IResult> CreatePlan(
        Guid studentId,
        [FromBody] CreateVocabPlanRequest request,
        VocabularyPlanService planService,
        CancellationToken cancellationToken)
    {
        if (!TryParseActor(request.Actor, out var actor, out var actorError))
        {
            return Error(400, "VOCAB_ACTOR_INVALID", actorError);
        }
        List<VocabPlanSegment> segments;
        if (request.Segments is not null)
        {
            if (!TryParseSegments(request.Segments, out var parsed, out var segmentError))
            {
                return segmentError;
            }
            segments = parsed;
        }
        else
        {
            var bookId = request.BookId?.Trim();
            if (string.IsNullOrEmpty(bookId))
            {
                return Error(400, "VOCAB_BOOK_ID_REQUIRED", "Book id is required.");
            }
            var unitIds = NormalizeUnitIds(request.UnitIds);
            if (unitIds is { Count: > 0 } && unitIds.Any(string.IsNullOrEmpty))
            {
                return Error(400, "VOCAB_UNIT_IDS_INVALID", "Unit ids must not be empty.");
            }
            segments = [new VocabPlanSegment(bookId, unitIds)];
        }
        if (request.DailyNewWords is < 1 or > 200)
        {
            return Error(400, "VOCAB_DAILY_NEW_WORDS_INVALID", "Daily new words must be between 1 and 200.");
        }

        try
        {
            var plan = await planService.CreateAsync(studentId, actor, segments, request.DailyNewWords, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = ToDto(plan) });
        }
        catch (Exception error)
        {
            return MapDomainError(error);
        }
    }

    private static async Task<IResult> UpdatePlan(
        Guid planId,
        [FromBody] UpdateVocabPlanRequest request,
        VocabularyPlanService planService,
        CancellationToken cancellationToken)
    {
        if (!TryParseActor(request.Actor, out var actor, out var actorError))
        {
            return Error(400, "VOCAB_ACTOR_INVALID", actorError);
        }
        if (request.Version is null)
        {
            return Error(400, "VOCAB_VERSION_REQUIRED", "The plan version is required.");
        }
        if (request.DailyNewWords is < 1 or > 200)
        {
            return Error(400, "VOCAB_DAILY_NEW_WORDS_INVALID", "Daily new words must be between 1 and 200.");
        }
        List<VocabPlanSegment>? segments = null;
        if (request.Segments is not null)
        {
            if (!TryParseSegments(request.Segments, out var parsed, out var segmentError))
            {
                return segmentError;
            }
            segments = parsed;
        }
        var bookId = string.IsNullOrWhiteSpace(request.BookId) ? null : request.BookId.Trim();
        var unitIds = NormalizeUnitIds(request.UnitIds);
        if (unitIds is { Count: > 0 } && unitIds.Any(string.IsNullOrEmpty))
        {
            return Error(400, "VOCAB_UNIT_IDS_INVALID", "Unit ids must not be empty.");
        }

        try
        {
            // segments (full ordered list) wins over the legacy first-book fields.
            var plan = segments is not null
                ? await planService.UpdateAsync(planId, actor, request.Version.Value, segments, request.DailyNewWords, cancellationToken).ConfigureAwait(false)
                : await planService.UpdateAsync(planId, actor, request.Version.Value, bookId, unitIds, request.DailyNewWords, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = ToDto(plan) });
        }
        catch (Exception error)
        {
            return MapDomainError(error);
        }
    }

    private static async Task<IResult> AdoptPlan(
        Guid planId,
        [FromBody] AdoptReleaseVocabPlanRequest request,
        VocabularyPlanService planService,
        CancellationToken cancellationToken)
    {
        if (request.TeacherId == Guid.Empty)
        {
            return Error(400, "VOCAB_TEACHER_ID_REQUIRED", "Teacher id is required.");
        }
        if (request.Version is null)
        {
            return Error(400, "VOCAB_VERSION_REQUIRED", "The plan version is required.");
        }

        try
        {
            var plan = await planService.AdoptAsync(planId, request.TeacherId, request.Version.Value, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = ToDto(plan) });
        }
        catch (Exception error)
        {
            return MapDomainError(error);
        }
    }

    private static async Task<IResult> ReleasePlan(
        Guid planId,
        [FromBody] AdoptReleaseVocabPlanRequest request,
        VocabularyPlanService planService,
        CancellationToken cancellationToken)
    {
        if (request.TeacherId == Guid.Empty)
        {
            return Error(400, "VOCAB_TEACHER_ID_REQUIRED", "Teacher id is required.");
        }
        if (request.Version is null)
        {
            return Error(400, "VOCAB_VERSION_REQUIRED", "The plan version is required.");
        }

        try
        {
            var plan = await planService.ReleaseAsync(planId, request.TeacherId, request.Version.Value, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = ToDto(plan) });
        }
        catch (Exception error)
        {
            return MapDomainError(error);
        }
    }

    private static IResult EndPlan([FromBody] EndVocabPlanRequest request)
    {
        if (!TryParseActor(request.Actor, out _, out var actorError))
            return Error(400, "VOCAB_ACTOR_INVALID", actorError);
        return Error(409, "VOCAB_ROUTINE_CONTINUOUS", "Daily vocabulary is continuous; update its learning scope instead.");
    }

    private static async Task<IResult> BatchGetPlans(
        [FromBody] BatchGetVocabPlansRequest request,
        VocabularyPlanService planService,
        CancellationToken cancellationToken)
    {
        if (request.PlanIds is null || request.PlanIds.Count == 0)
        {
            return Error(400, "VOCAB_PLAN_IDS_REQUIRED", "At least one plan id is required.");
        }
        if (request.PlanIds.Count > 100)
        {
            return Error(400, "VOCAB_PLAN_IDS_TOO_MANY", "At most 100 plan ids are allowed per request.");
        }

        var plans = await planService.ListByIdsAsync(request.PlanIds, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { success = true, data = new { plans = plans.Select(ToDto).ToList() } });
    }


    private static bool TryParseActor(VocabPlanActorRequest? request, out VocabPlanActor actor, out string error)
    {
        actor = VocabPlanActor.AsStudent();
        error = string.Empty;
        if (request is null)
        {
            error = "Actor is required.";
            return false;
        }

        switch ((request.Type ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "student":
                actor = VocabPlanActor.AsStudent();
                return true;
            case "teacher":
                if (request.TeacherId is null || request.TeacherId == Guid.Empty)
                {
                    error = "Teacher id is required for a teacher actor.";
                    return false;
                }
                actor = VocabPlanActor.AsTeacher(request.TeacherId.Value);
                return true;
            default:
                error = "Actor type must be 'student' or 'teacher'.";
                return false;
        }
    }

    private static List<string>? NormalizeUnitIds(List<string>? unitIds) =>
        unitIds?.Select(unitId => (unitId ?? string.Empty).Trim()).ToList();

    /// <summary>Request shape check for the ordered book list; duplicate books are rejected by the domain service.</summary>
    private static bool TryParseSegments(List<VocabPlanSegmentRequest?> request, out List<VocabPlanSegment> segments, out IResult error)
    {
        segments = [];
        error = Results.Empty;
        if (request.Count is < 1 or > 20)
        {
            error = Error(400, "VOCAB_PLAN_SCOPE_INVALID", "Send between one and 20 books.");
            return false;
        }
        foreach (var item in request)
        {
            var bookId = item?.BookId?.Trim();
            if (string.IsNullOrEmpty(bookId))
            {
                error = Error(400, "VOCAB_BOOK_ID_REQUIRED", "Book id is required.");
                return false;
            }
            var unitIds = NormalizeUnitIds(item!.UnitIds);
            if (unitIds is { Count: > 0 } && unitIds.Any(string.IsNullOrEmpty))
            {
                error = Error(400, "VOCAB_UNIT_IDS_INVALID", "Unit ids must not be empty.");
                return false;
            }
            segments.Add(new VocabPlanSegment(bookId, unitIds is { Count: > 0 } ? unitIds : null));
        }
        return true;
    }

    private static object ToSegmentsDto(VocabPlanModel plan) =>
        plan.Segments.Select(segment => new { segment.BookId, unitIds = segment.UnitIds }).ToList();

    /// <summary>bookId / unitIds mirror the first segment so consumers that predate multi-book scopes keep working.</summary>
    private static object? ToDto(VocabPlanModel? plan) => plan is null ? null : new
    {
        plan.Id,
        plan.StudentId,
        plan.BookId,
        unitIds = plan.UnitIds,
        segments = ToSegmentsDto(plan),
        plan.DailyNewWords,
        controller = plan.Controller.ToString(),
        plan.ControllerTeacherId,
        status = plan.Status.ToString(),
        plan.StartDate,
        plan.TotalWords,
        plan.Version,
        plan.CreatedAt,
        plan.UpdatedAt,
    };
    private static object? ToActiveDto(VocabPlanModel? plan, DateTimeOffset? teacherAdjustedAt) => plan is null ? null : new
    {
        plan.Id,
        plan.StudentId,
        plan.BookId,
        unitIds = plan.UnitIds,
        segments = ToSegmentsDto(plan),
        plan.DailyNewWords,
        controller = plan.Controller.ToString(),
        plan.ControllerTeacherId,
        status = plan.Status.ToString(),
        plan.StartDate,
        plan.TotalWords,
        plan.Version,
        plan.CreatedAt,
        plan.UpdatedAt,
        teacherAdjustedAt,
    };

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { success = false, code, message }, statusCode: statusCode);

    private static IResult MapDomainError(Exception error) => error switch
    {
        VocabPlanNotFoundException => Error(404, "VOCAB_PLAN_NOT_FOUND", error.Message),
        VocabPlanActiveExistsException => Error(409, "VOCAB_PLAN_ACTIVE_EXISTS", error.Message),
        VocabPlanVersionConflictException => Error(409, "VOCAB_PLAN_VERSION_CONFLICT", error.Message),
        VocabPlanNotActiveException => Error(409, "VOCAB_PLAN_NOT_ACTIVE", error.Message),
        VocabPlanForbiddenException => Error(403, "VOCAB_PLAN_FORBIDDEN", error.Message),
        VocabPlanScopeEmptyException => Error(400, "VOCAB_PLAN_SCOPE_EMPTY", error.Message),
        VocabPlanScopeInvalidException => Error(400, "VOCAB_PLAN_SCOPE_INVALID", error.Message),
        LexarborCatalogException catalog => catalog.ErrorCode switch
        {
            BookNotFound => Error(404, catalog.ErrorCode, catalog.Message),
            BookDisabled => Error(422, catalog.ErrorCode, catalog.Message),
            UnitNotFound => Error(404, catalog.ErrorCode, catalog.Message),
            _ => Error(503, catalog.ErrorCode, catalog.Message),
        },
        _ => Error(500, "VOCAB_PLAN_INTERNAL", "An unexpected error occurred."),
    };
}


public sealed class VocabPlanActorRequest
{

    public string? Type { get; set; }

    public Guid? TeacherId { get; set; }
}

public sealed class VocabPlanSegmentRequest
{
    public string? BookId { get; set; }

    public List<string>? UnitIds { get; set; }
}

public sealed class CreateVocabPlanRequest
{
    public VocabPlanActorRequest? Actor { get; set; }

    public List<VocabPlanSegmentRequest?>? Segments { get; set; }

    public string? BookId { get; set; }

    public List<string>? UnitIds { get; set; }

    public int DailyNewWords { get; set; }
}

public sealed class UpdateVocabPlanRequest
{
    public VocabPlanActorRequest? Actor { get; set; }

    public int? Version { get; set; }

    public List<VocabPlanSegmentRequest?>? Segments { get; set; }

    public string? BookId { get; set; }

    public List<string>? UnitIds { get; set; }

    public int? DailyNewWords { get; set; }
}

public sealed class AdoptReleaseVocabPlanRequest
{
    public Guid TeacherId { get; set; }

    public int? Version { get; set; }
}

public sealed class EndVocabPlanRequest
{
    public VocabPlanActorRequest? Actor { get; set; }
}

public sealed class BatchGetVocabPlansRequest
{
    public List<Guid>? PlanIds { get; set; }
}
