using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Learning.Service;

public static partial class VocabularyNotebookHttpEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyFamilyHttpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vocabulary/students/{studentId:guid}/word-family");
        group.MapGet("/sources", SourcesAsync);
        group.MapGet("/candidates", FamilyCandidatesAsync);
        group.MapPost("/items", AddFamilyItemsAsync);
        return app;
    }

    private static Task<IResult> SourcesAsync(Guid studentId, string? actor, Guid? teacherId, string? q, int? page, int? size,
        VocabularyFamilyService service, CancellationToken cancellationToken) =>
        WithFamilyActorAsync(new VocabNotebookActorRequest { Type = actor, TeacherId = teacherId }, async parsed =>
            await service.SourcesAsync(studentId, parsed, q, page ?? 1, size ?? 20, cancellationToken).ConfigureAwait(false));

    private static Task<IResult> FamilyCandidatesAsync(Guid studentId, string? actor, Guid? teacherId, string? wordKey,
        VocabularyFamilyService service, CancellationToken cancellationToken) =>
        WithFamilyActorAsync(new VocabNotebookActorRequest { Type = actor, TeacherId = teacherId }, async parsed =>
            await service.CandidatesAsync(studentId, parsed, wordKey ?? string.Empty, cancellationToken).ConfigureAwait(false));

    private static Task<IResult> AddFamilyItemsAsync(Guid studentId, [FromBody] FamilyAddRequest request,
        VocabularyFamilyService service, CancellationToken cancellationToken) =>
        WithFamilyActorAsync(request.Actor, async parsed =>
            new { items = await service.AddAsync(studentId, parsed, request.SourceWordKey, request.Items, cancellationToken).ConfigureAwait(false) });

    private static async Task<IResult> WithFamilyActorAsync<T>(VocabNotebookActorRequest? request, Func<VocabNotebookActor, Task<T>> work)
    {
        if (!TryParseActor(request, out var actor, out var actorError)) return actorError;
        try
        {
            var data = await work(actor).ConfigureAwait(false);
            return Results.Ok(new { success = true, data });
        }
        catch (VocabularyFamilySourceForbiddenException error)
        {
            return Error(403, "VOCAB_FAMILY_SOURCE_FORBIDDEN", error.Message);
        }
        catch (VocabNotebookInvalidException)
        {
            return Error(400, "VOCAB_FAMILY_INVALID", "The word-family request is invalid.");
        }
        catch (Exception error) when (IsMapped(error))
        {
            return MapError(error);
        }
    }

    private sealed record FamilyAddRequest(VocabNotebookActorRequest? Actor, string? SourceWordKey, IReadOnlyList<VocabularyFamilySelection?>? Items);
}
