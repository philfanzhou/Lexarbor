using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

/// <summary>Exact, book-scoped writes of meaning positions.</summary>
public static class VocabularyMeaningPositionEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyMeaningPositionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/vocabulary-books/{bookId}/meanings/{meaningId}/positions")
            .RequireAuthorization("VocabularyAdmin");
        group.MapPut("/", Move);
        group.MapDelete("/{unitId}", Remove);
        return app;
    }

    private static async Task<IResult> Move(string bookId, string meaningId,
        [FromBody] VocabularyMeaningPositionMoveRequest request,
        VocabularyBookUnitDomainService service, CancellationToken cancellationToken)
    {
        if (request.From is null || request.To is null)
            return VocabularyHttpResponse.BadRequest("From and to positions are required.");
        await service.MovePositionAsync(bookId, meaningId,
            request.From.UnitId, request.From.Section, request.From.EntryKind,
            request.To.UnitId, request.To.Section, request.To.EntryKind, cancellationToken);
        return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
    }

    private static async Task<IResult> Remove(string bookId, string meaningId, string unitId,
        HttpRequest request, VocabularyBookUnitDomainService service, CancellationToken cancellationToken)
    {
        if (!request.Query.TryGetValue("section", out var section) || section.Count != 1 ||
            !request.Query.TryGetValue("entryKind", out var entryKind) || entryKind.Count != 1)
            return VocabularyHttpResponse.BadRequest("Section and entryKind must be specified.");
        var normalizedSection = FromQuery(section.ToString(), "Section", "A", "B");
        var normalizedKind = FromQuery(entryKind.ToString(), "EntryKind", "word", "phrase");
        if (normalizedSection.Error is not null) return VocabularyHttpResponse.BadRequest(normalizedSection.Error);
        if (normalizedKind.Error is not null) return VocabularyHttpResponse.BadRequest(normalizedKind.Error);
        await service.RemovePositionAsync(bookId, meaningId, unitId, normalizedSection.Value, normalizedKind.Value,
            cancellationToken);
        return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
    }

    private static (string? Value, string? Error) FromQuery(string raw, string name, string first, string second)
        => raw switch
        {
            "none" => (null, null),
            var value when value == first || value == second => (value, null),
            _ => (null, $"{name} must be {first}, {second}, or none.")
        };
}
