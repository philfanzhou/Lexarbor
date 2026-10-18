using System.Globalization;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class TextbookAudioTrackEndpoints
{
    public static IEndpointRouteBuilder MapTextbookAudioTrackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks")
            .RequireAuthorization(AdminEndpointAuthorization.PolicyName);
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{trackId}", ReplaceAsync);
        group.MapDelete("/{trackId}", DeleteAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(string bookId, string unitId, HttpRequest request,
        TextbookAudioTrackService service, CancellationToken cancellationToken)
    {
        if (!TryPage(request, "page", out var page) || !TryPage(request, "size", out var size))
            return VocabularyHttpResponse.BadRequest("Page and size must be nonnegative integers.");
        var result = await service.ListAsync(bookId, unitId, page, size, cancellationToken);
        return VocabularyHttpResponse.Ok(new { items = result.Items.Select(TextbookAudioTrackDto.FromModel), result.TotalCount, result.TotalPage });
    }
    private static bool TryPage(HttpRequest request, string key, out int number)
    {
        number = 0;
        return !request.Query.TryGetValue(key, out var value) || value.Count == 1 &&
            int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
    }
    private static async Task<IResult> CreateAsync(string bookId, string unitId, [FromBody] TextbookAudioTrackCreateRequest request,
        TextbookAudioTrackService service, CancellationToken cancellationToken)
        => VocabularyHttpResponse.Ok(TextbookAudioTrackDto.FromModel(await service.CreateAsync(bookId, unitId, request.Title, request.DisplayOrder, cancellationToken)));
    private static async Task<IResult> ReplaceAsync(string bookId, string unitId, string trackId, [FromBody] TextbookAudioTrackReplaceRequest request,
        TextbookAudioTrackService service, CancellationToken cancellationToken)
        => VocabularyHttpResponse.Ok(TextbookAudioTrackDto.FromModel(await service.ReplaceAsync(bookId, unitId, trackId,
            request.Title, request.DisplayOrder, request.IsAvailable, request.ExpectedVersion, cancellationToken)));
    private static async Task<IResult> DeleteAsync(string bookId, string unitId, string trackId, HttpRequest request,
        TextbookAudioTrackService service, CancellationToken cancellationToken)
    {
        var expected = request.Query["expectedVersion"];
        if (expected.Count != 1) return VocabularyHttpResponse.BadRequest("Expected version is required.");
        await service.DeleteAsync(bookId, unitId, trackId, expected[0], cancellationToken);
        return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
    }
}
