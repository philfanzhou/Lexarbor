using System.Diagnostics;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Learning.Service;

public static partial class VocabularyNotebookHttpEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyNotebookHttpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vocabulary");
        group.MapGet("/students/{studentId:guid}/notebook", List);
        group.MapPost("/students/{studentId:guid}/notebook/items", Add);
        group.MapPost("/students/{studentId:guid}/notebook/items/{itemId:guid}/remove", Remove);
        group.MapPost("/students/{studentId:guid}/notebook/items/{itemId:guid}/resubmit", Resubmit);
        group.MapPost("/students/{studentId:guid}/notebook/photo", Photo).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> List(Guid studentId, VocabularyNotebookService service, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await service.ListAsync(studentId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = new { items = entries.Select(ToDto).ToList() } });
        }
        catch (Exception error) when (IsMapped(error))
        {
            return MapError(error);
        }
    }

    private static async Task<IResult> Add(Guid studentId, [FromBody] VocabNotebookAddRequest request,
        VocabularyNotebookService service, CancellationToken cancellationToken)
    {
        if (!TryParseActor(request.Actor, out var actor, out var actorError)) return actorError;
        try
        {
            var entry = string.IsNullOrWhiteSpace(request.MeaningId)
                ? await service.SubmitAsync(studentId, actor, request.Word, request.PartOfSpeech, request.Meaning, cancellationToken).ConfigureAwait(false)
                : await service.AddExistingAsync(studentId, actor, request.Word, request.MeaningId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = ToDto(entry) });
        }
        catch (Exception error) when (IsMapped(error))
        {
            return MapError(error);
        }
    }

    private static async Task<IResult> Remove(Guid studentId, Guid itemId, [FromBody] VocabNotebookRemoveRequest request,
        VocabularyNotebookService service, CancellationToken cancellationToken)
    {
        if (!TryParseActor(request.Actor, out var actor, out var actorError)) return actorError;
        try
        {
            await service.RemoveAsync(studentId, actor, itemId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true });
        }
        catch (Exception error) when (IsMapped(error))
        {
            return MapError(error);
        }
    }

    /// <summary>Edits a rejected item and submits it to Lexarbor again; either actor may resubmit any rejected item.</summary>
    private static async Task<IResult> Resubmit(Guid studentId, Guid itemId, [FromBody] VocabNotebookResubmitRequest request,
        VocabularyNotebookService service, CancellationToken cancellationToken)
    {
        if (!TryParseActor(request.Actor, out _, out var actorError)) return actorError;
        try
        {
            var entry = await service.ResubmitAsync(studentId, itemId, request.Word, request.PartOfSpeech, request.Meaning, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new { success = true, data = ToDto(entry) });
        }
        catch (Exception error) when (IsMapped(error))
        {
            return MapError(error);
        }
    }
    private static async Task<IResult> Photo(Guid studentId, HttpRequest request, VocabularyPhotoImportService service,
        VocabularyPhotoTrace trace, VocabularyPhotoTraceStore traces, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType) return MapError(new VocabPhotoInvalidException());
        IFormFile? file;
        try
        {
            var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            file = form.Files.Count == 1 && form.Files[0].Name == "file" ? form.Files[0] : null;
        }
        // BadHttpRequestException (a body over the server limit) is an IOException.
        catch (Exception error) when (error is InvalidDataException or IOException)
        {
            file = null;
        }

        if (file is null || file.Length is 0 or > VocabularyPhotoImportService.MaxPhotoBytes)
            return MapError(new VocabPhotoInvalidException());
        byte[] photo;
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            photo = buffer.ToArray();
        }

        var at = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var contentType = file.ContentType ?? string.Empty;
        var outcome = "failed";
        string? message = null;
        List<object>? result = null;
        try
        {
            var candidates = await service.PreviewAsync(studentId, new VocabPhoto(photo, contentType), cancellationToken).ConfigureAwait(false);
            result = candidates.Select(ToDto).ToList();
            outcome = "ok";
            return Results.Ok(new { success = true, data = new { candidates = result } });
        }
        catch (Exception error) when (IsMapped(error))
        {
            (outcome, message) = (TraceOutcome(error), error.Message);
            return MapError(error);
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        catch (Exception error)
        {
            // Only the type: an unexpected message is not known to be free of private details.
            message = error.GetType().Name;
            throw;
        }
        finally
        {
            // Only a request that reached StructaDoc says anything about recognition worth keeping.
            if (trace.RecognitionMilliseconds is not null)
                await traces.SaveAsync(new VocabularyPhotoTraceRecord(at, studentId, photo, contentType, outcome, message,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, trace, result)).ConfigureAwait(false);
        }
    }

    private static string TraceOutcome(Exception error) => error switch
    {
        VocabPhotoInvalidException => "invalid",
        VocabPhotoImportUnavailableException => "unavailable",
        VocabCatalogUnavailableException => "catalog-unavailable",
        _ => "failed",
    };

    private static object ToDto(VocabPhotoCandidate candidate) => new
    {
        candidate.Word,
        candidate.Recognized,
        candidate.PartOfSpeech,
        candidate.Meaning,
        state = candidate.State switch
        {
            VocabPhotoCandidateState.Matched => "matched",
            VocabPhotoCandidateState.Choose => "choose",
            _ => "new",
        },
        candidate.MeaningId,
        meanings = candidate.Meanings.Select(meaning => new
        {
            meaning.MeaningId,
            meaning.Word,
            meaning.WordKey,
            meaning.PartOfSpeech,
            meaning.Meaning,
            meaning.MeaningKey,
            meaning.BookName,
        }).ToList(),
    };

    private static object ToDto(VocabNotebookEntry entry) => new
    {
        entry.Item.Id,
        entry.Item.WordKey,
        entry.Item.MeaningKey,
        entry.Item.Word,
        entry.Item.PartOfSpeech,
        entry.Item.Meaning,
        status = entry.Item.Status switch
        {
            VocabNotebookItemStatus.Pending => "pending",
            VocabNotebookItemStatus.Rejected => "rejected",
            _ => "ready",
        },
        entry.Item.ReviewComment,
        // The revised mark: what was entered before review changed the text; null on other items.
        original = entry.Item.Original is { } original
            ? new { original.Word, original.PartOfSpeech, original.Meaning }
            : null,
        familySources = entry.Item.FamilySources.Select(source => new
        { source.Word, source.RelationType, source.Direction }).Distinct().ToArray(),
        familySource = entry.Item.FamilySources.FirstOrDefault(source => source.HasMeaningPair) is { } family
            ? new { family.Word, family.RelationType, family.Direction }
            : null,
        addedBy = entry.Item.AddedBy == VocabNotebookActorType.Teacher ? "teacher" : "student",
        entry.Item.AddedByTeacherId,
        entry.Item.AddedAt,
        entry.Learned,
    };

    private static bool TryParseActor(VocabNotebookActorRequest? request, out VocabNotebookActor actor, out IResult error)
    {
        actor = VocabNotebookActor.AsStudent();
        error = Results.Empty;
        switch ((request?.Type ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "student":
                return true;
            case "teacher" when request!.TeacherId is { } teacherId && teacherId != Guid.Empty:
                actor = VocabNotebookActor.AsTeacher(teacherId);
                return true;
            default:
                error = Error(400, "VOCAB_NOTEBOOK_ACTOR_INVALID", "Actor must be a student, or a teacher with a teacher id.");
                return false;
        }
    }

    private static bool IsMapped(Exception error) => error is VocabStudentNotFoundException or VocabNotebookInvalidException
        or VocabNotebookItemNotFoundException or VocabNotebookMeaningNotFoundException or VocabNotebookRemovalForbiddenException
        or VocabNotebookItemNotRejectedException or VocabCatalogUnavailableException
        or VocabPhotoInvalidException or VocabPhotoImportUnavailableException;

    private static IResult MapError(Exception error) => error switch
    {
        VocabStudentNotFoundException => Error(404, "VOCAB_STUDENT_NOT_FOUND", error.Message),
        VocabNotebookInvalidException => Error(400, "VOCAB_NOTEBOOK_INVALID", error.Message),
        VocabNotebookItemNotFoundException => Error(404, "VOCAB_NOTEBOOK_ITEM_NOT_FOUND", error.Message),
        VocabNotebookMeaningNotFoundException => Error(404, "VOCAB_NOTEBOOK_MEANING_NOT_FOUND", error.Message),
        VocabNotebookRemovalForbiddenException => Error(403, "VOCAB_NOTEBOOK_REMOVE_FORBIDDEN", error.Message),
        VocabNotebookItemNotRejectedException => Error(409, "VOCAB_NOTEBOOK_ITEM_NOT_REJECTED", error.Message),
        VocabPhotoInvalidException => Error(400, "VOCAB_PHOTO_INVALID", error.Message),
        VocabPhotoImportUnavailableException => Error(503, "VOCAB_PHOTO_IMPORT_UNAVAILABLE", "Photo import is temporarily unavailable."),
        _ => Error(503, "VOCAB_CATALOG_UNAVAILABLE", "The vocabulary catalog is temporarily unavailable."),
    };

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { success = false, code, message }, statusCode: statusCode);
}

public sealed class VocabNotebookActorRequest
{

    public string? Type { get; set; }

    public Guid? TeacherId { get; set; }
}

public sealed class VocabNotebookAddRequest
{
    public VocabNotebookActorRequest? Actor { get; set; }
    public string? Word { get; set; }
    public string? MeaningId { get; set; }
    public string? PartOfSpeech { get; set; }
    public string? Meaning { get; set; }
}

public sealed class VocabNotebookRemoveRequest
{
    public VocabNotebookActorRequest? Actor { get; set; }
}

public sealed class VocabNotebookResubmitRequest
{
    public VocabNotebookActorRequest? Actor { get; set; }
    public string? Word { get; set; }
    public string? PartOfSpeech { get; set; }
    public string? Meaning { get; set; }
}
