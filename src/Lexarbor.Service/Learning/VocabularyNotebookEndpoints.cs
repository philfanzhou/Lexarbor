using System.Diagnostics;
using System.Text.Json.Serialization;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Learning.Service;

public static partial class VocabularyNotebookEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyNotebook(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vocabulary");
        group.MapGet("/students/{studentId:guid}/notebook", List);
        group.MapGet("/students/{studentId:guid}/notebook/summary", Summary);
        group.MapGet("/students/{studentId:guid}/notebook/query", Query);
        group.MapPost("/students/{studentId:guid}/notebook/membership", Membership);
        group.MapPost("/students/{studentId:guid}/notebook/items", Add);
        group.MapPost("/students/{studentId:guid}/notebook/items/{itemId:guid}/remove", Remove);
        group.MapPost("/students/{studentId:guid}/notebook/items/{itemId:guid}/resubmit", Resubmit);
        group.MapPost("/students/{studentId:guid}/notebook/photo", Photo).DisableAntiforgery();
        MapPhotoTasks(group);
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
    private static async Task<VocabPhoto?> ReadPhoto(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType) return null;
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
            return null;
        byte[] photo;
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            photo = buffer.ToArray();
        }

        return new VocabPhoto(photo, file.ContentType ?? string.Empty);
    }

    private static async Task<IResult> Photo(Guid studentId, HttpRequest request, VocabularyPhotoImportService service,
        VocabularyPhotoTrace trace, VocabularyPhotoTraceStore traces, CancellationToken cancellationToken)
    {
        var upload = await ReadPhoto(request, cancellationToken);
        if (upload is null) return MapError(new VocabPhotoInvalidException());
        try
        {
            var candidates = await PreviewWithTraceAsync(studentId, upload, service, trace, traces, cancellationToken);
            return Results.Ok(new { success = true, data = new { candidates = candidates.Select(ToDto) } });
        }
        catch (Exception error) when (IsMapped(error))
        {
            return MapError(error);
        }
    }

    internal static async Task<IReadOnlyList<VocabPhotoCandidate>> PreviewWithTraceAsync(Guid studentId, VocabPhoto upload,
        VocabularyPhotoImportService service, VocabularyPhotoTrace trace, VocabularyPhotoTraceStore traces, CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        string? message = null;
        List<object>? result = null;
        try
        {
            var candidates = await service.PreviewAsync(studentId, upload, cancellationToken).ConfigureAwait(false);
            result = candidates.Select(ToDto).ToList();
            outcome = "ok";
            return candidates;
        }
        catch (Exception error) when (IsMapped(error))
        {
            (outcome, message) = (TraceOutcome(error), error.Message);
            throw;
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
            // Only a request that reached the vision model says anything about recognition worth keeping.
            if (trace.RecognitionMilliseconds is not null)
                await traces.SaveAsync(new VocabularyPhotoTraceRecord(at, studentId, upload.Content, upload.ContentType, outcome, message,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, trace, result)).ConfigureAwait(false);
        }
    }

    private static string TraceOutcome(Exception error) => error switch
    {
        VocabPhotoInvalidException => "invalid",
        VocabPhotoUnavailableException => "unavailable",
        VocabCatalogUnavailableException => "catalog-unavailable",
        _ => "failed",
    };

    private sealed record PhotoMeaningDto(string MeaningId, string Word, string WordKey, string? PartOfSpeech,
        string Meaning, string MeaningKey, string BookName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? InNotebook);

    private static object ToDto(VocabPhotoCandidate candidate) => ToDto(candidate, null);

    private static object ToDto(VocabPhotoCandidate candidate, IReadOnlySet<CardKey>? included) => new
    {
        candidate.Word,
        candidate.Recognized,
        candidate.Review,
        candidate.PartOfSpeech,
        candidate.Meaning,
        state = candidate.State switch
        {
            VocabPhotoCandidateState.Matched => "matched",
            VocabPhotoCandidateState.Choose => "choose",
            _ => "new",
        },
        candidate.MeaningId,
        meanings = candidate.Meanings.Select(meaning => new PhotoMeaningDto(
            meaning.MeaningId, meaning.Word, meaning.WordKey, meaning.PartOfSpeech, meaning.Meaning,
            meaning.MeaningKey, meaning.BookName, included?.Contains(new CardKey(meaning.WordKey, meaning.MeaningKey)))).ToList(),
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
                error = ApiResponse.Failure(400, "VOCAB_NOTEBOOK_ACTOR_INVALID", "Actor must be a student, or a teacher with a teacher id.");
                return false;
        }
    }

    private static bool IsMapped(Exception error) => error is VocabStudentNotFoundException or VocabNotebookInvalidException
        or VocabNotebookItemNotFoundException or VocabNotebookMeaningNotFoundException or VocabNotebookRemovalForbiddenException
        or VocabNotebookItemNotRejectedException or VocabCatalogUnavailableException
        or VocabPhotoInvalidException or VocabPhotoUnavailableException;

    private static IResult MapError(Exception error) => error switch
    {
        VocabStudentNotFoundException => ApiResponse.Failure(404, "VOCAB_STUDENT_NOT_FOUND", error.Message),
        VocabNotebookInvalidException => ApiResponse.Failure(400, "VOCAB_NOTEBOOK_INVALID", error.Message),
        VocabNotebookItemNotFoundException => ApiResponse.Failure(404, "VOCAB_NOTEBOOK_ITEM_NOT_FOUND", error.Message),
        VocabNotebookMeaningNotFoundException => ApiResponse.Failure(404, "VOCAB_NOTEBOOK_MEANING_NOT_FOUND", error.Message),
        VocabNotebookRemovalForbiddenException => ApiResponse.Failure(403, "VOCAB_NOTEBOOK_REMOVE_FORBIDDEN", error.Message),
        VocabNotebookItemNotRejectedException => ApiResponse.Failure(409, "VOCAB_NOTEBOOK_ITEM_NOT_REJECTED", error.Message),
        VocabPhotoInvalidException => ApiResponse.Failure(400, "VOCAB_PHOTO_INVALID", error.Message),
        VocabPhotoUnavailableException => ApiResponse.Failure(503, "VOCAB_PHOTO_IMPORT_UNAVAILABLE", "Photo import is temporarily unavailable."),
        _ => ApiResponse.Failure(503, "VOCAB_CATALOG_UNAVAILABLE", "The vocabulary catalog is temporarily unavailable."),
    };
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
