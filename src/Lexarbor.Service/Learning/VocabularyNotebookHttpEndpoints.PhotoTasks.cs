using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Lexarbor.Learning.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Learning.Service;

public static partial class VocabularyNotebookHttpEndpoints
{
    private static void MapPhotoTasks(RouteGroupBuilder notebook)
    {
        var tasks = notebook.MapGroup("/students/{studentId:guid}/notebook/photo-tasks");
        tasks.MapPost("", CreatePhotoTask).DisableAntiforgery();
        tasks.MapGet("", ListPhotoTasks);
        tasks.MapGet("/{taskId:guid}", GetPhotoTask);
        tasks.MapPost("/{taskId:guid}/retry", RetryPhotoTask);
        tasks.MapPut("/{taskId:guid}/entries/{index:int}", AddPhotoEntry);
        tasks.MapDelete("/{taskId:guid}", DeletePhotoTask);
    }

    private static IResult MissingPhotoTask() => Error(404, "VOCAB_PHOTO_TASK_NOT_FOUND", "Photo import task not found.");
    private static IResult PhotoTaskConflict() => Error(409, "VOCAB_PHOTO_TASK_STATE", "The task is not in the required state.");

    private static async Task<IResult> CreatePhotoTask(Guid studentId, HttpRequest request, PhotoImportTaskRepository tasks, CancellationToken ct)
    {
        var photo = await ReadPhoto(request, ct);
        if (photo is null) return MapError(new VocabPhotoInvalidException());
        try { VocabularyPhotoImportService.RequireImage(photo); }
        catch (VocabPhotoInvalidException error) { return MapError(error); }
        var id = await tasks.CreateAsync(studentId, photo.Content, ct);
        if (id is null) return MapError(new VocabStudentNotFoundException());
        return Results.Accepted($"{request.PathBase}{request.Path.ToString().TrimEnd('/')}/{id}",
            new { success = true, data = new { id, state = "queued" } });
    }

    private static async Task<IResult> ListPhotoTasks(Guid studentId, int? page, ILearnerDirectory learners,
        PhotoImportTaskRepository tasks, CancellationToken ct)
    {
        if (page is < 1) return Error(400, "VOCAB_PHOTO_TASK_PAGE_INVALID", "Page must be a positive integer.");
        if (!await learners.ExistsAsync(studentId, ct)) return MapError(new VocabStudentNotFoundException());
        var items = await tasks.ListAsync(studentId, page ?? 1, ct);
        return Results.Ok(new { success = true, data = new { items, page = page ?? 1, pageSize = 50 } });
    }

    private static async Task<IResult> GetPhotoTask(Guid studentId, Guid taskId, PhotoImportTaskRepository tasks,
        IVocabNotebookRepository notebook, CancellationToken ct)
    {
        var task = await tasks.ReadAsync(studentId, taskId, false, ct);
        if (task is null) return MissingPhotoTask();
        var entries = task.ReadEntries();
        var keys = entries.SelectMany(entry => entry.Candidate.Meanings)
            .Select(meaning => new CardKey(meaning.WordKey, meaning.MeaningKey)).ToHashSet();
        HashSet<CardKey> included = keys.Count == 0 ? [] : (await notebook.MembershipAsync(studentId, keys, ct)).ToHashSet();
        return Results.Ok(new
        {
            success = true,
            data = new
            {
                task.Id,
                task.State,
                task.CreatedAt,
                task.UpdatedAt,
                task.Attempts,
                task.ErrorCode,
                task.AddedCount,
                entries = entries.Select((entry, index) => new { index, candidate = ToDto(entry.Candidate, included), entry.NotebookItemId })
            }
        });
    }

    private static async Task<IResult> RetryPhotoTask(Guid studentId, Guid taskId, PhotoImportTaskRepository tasks, CancellationToken ct)
    {
        if (await tasks.RetryAsync(studentId, taskId, ct) == 1)
            return Results.Accepted(value: new { success = true, data = new { id = taskId, state = "queued" } });
        return await tasks.ReadAsync(studentId, taskId, false, ct) is null ? MissingPhotoTask() : PhotoTaskConflict();
    }

    private static async Task<IResult> DeletePhotoTask(Guid studentId, Guid taskId, PhotoImportTaskRepository tasks, CancellationToken ct) =>
        await tasks.DeleteAsync(studentId, taskId, ct) == 1 ? Results.NoContent() : MissingPhotoTask();

    public sealed record PhotoEntryChoice(string? MeaningId, string? Word, string? PartOfSpeech, string? Meaning);

    private static async Task<IResult> AddPhotoEntry(Guid studentId, Guid taskId, int index, PhotoEntryChoice choice,
        PhotoImportTaskRepository tasks, IVocabNotebookRepository repository, VocabularyNotebookService notebook, CancellationToken ct)
    {
        try
        {
            return await repository.InStudentWriteAsync<IResult>(studentId, async () =>
            {
                var task = await tasks.ReadAsync(studentId, taskId, true, ct);
                if (task is null) return MissingPhotoTask();
                if (task.State != "ready") return PhotoTaskConflict();
                var entries = task.ReadEntries();
                if (index < 0 || index >= entries.Count) return Error(404, "VOCAB_PHOTO_ENTRY_NOT_FOUND", "Photo entry not found.");
                var entry = entries[index];
                if (entry.NotebookItemId is { } existing) return PhotoEntryAdded(existing, false);
                var candidate = entry.Candidate;
                var word = choice.Word ?? candidate.Word;
                var meaningId = choice.MeaningId;
                if (candidate.State == VocabPhotoCandidateState.Choose && meaningId is null && string.IsNullOrWhiteSpace(choice.Meaning))
                    return Error(400, "VOCAB_PHOTO_CHOICE_REQUIRED", "Choose a catalog meaning or provide a definition.");
                var edited = choice.Word is not null || choice.Meaning is not null || choice.PartOfSpeech is not null;
                if (!edited && meaningId is null)
                    meaningId = candidate.MeaningId;
                var actor = VocabNotebookActor.AsStudent();
                var saved = meaningId is null
                    ? await notebook.SubmitAsync(studentId, actor, word, choice.PartOfSpeech ?? candidate.PartOfSpeech,
                        choice.Meaning ?? candidate.Meaning, ct)
                    : await notebook.AddExistingAsync(studentId, actor, word, meaningId, ct);
                entries[index] = entry with { NotebookItemId = saved.Item.Id };
                var added = task.AddedCount + 1;
                await tasks.SaveProgressAsync(studentId, taskId, entries, added, ct);
                return PhotoEntryAdded(saved.Item.Id, added == entries.Count);
            }, ct);
        }
        catch (Exception error) when (IsMapped(error)) { return MapError(error); }
        catch (VocabNotebookWriteConflictException)
        {
            return Error(409, "VOCAB_NOTEBOOK_WRITE_CONFLICT", "The notebook changed. Retry this entry.");
        }
    }

    private static IResult PhotoEntryAdded(Guid notebookItemId, bool taskDeleted) =>
        Results.Ok(new { success = true, data = new { notebookItemId, taskDeleted } });
}
