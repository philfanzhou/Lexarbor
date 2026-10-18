using System.Globalization;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class PublicTextbookAudioEndpoints
{
    private sealed record NoStoreMetadata;

    // Register before rate limiting: even rejected requests and exception responses
    // must carry the route's cache contract. OnStarting survives response clearing.
    public static IApplicationBuilder UsePublicTextbookAudioNoStore(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<NoStoreMetadata>() != null)
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    return Task.CompletedTask;
                });
            await next(context);
        });

    public static void MapPublicTextbookAudioEndpoints(this IEndpointRouteBuilder app, string? publicApiRateLimitPolicy = null)
    {
        var group = app.MapGroup("/api/vocabulary-books/{bookId}/units/{unitId}/audio-tracks")
            .WithMetadata(new NoStoreMetadata()).AllowAnonymous();
        if (!string.IsNullOrWhiteSpace(publicApiRateLimitPolicy)) group.RequireRateLimiting(publicApiRateLimitPolicy);
        group.MapGet("/", async (string bookId, string unitId, HttpRequest request,
            ITextbookAudioPlaybackOperationFactory operations, CancellationToken cancellationToken) =>
        {
            if (!Valid(bookId, unitId)) return VocabularyHttpResponse.BadRequest("An ID is required.");
            if (!TryPage(request, "page", out var page) || !TryPage(request, "size", out var size))
                return VocabularyHttpResponse.BadRequest("Page and size must be nonnegative integers.");
            using var operation = await operations.AcquireAsync(cancellationToken);
            if (!operation.Enabled) return Error("Audio storage is disabled.");
            var result = await operation.Playback.ListPublicAsync(bookId, unitId, page, size, cancellationToken);
            return VocabularyHttpResponse.Ok(new { items = result.Items.Select(PublicTextbookAudioTrackDto.FromModel), result.TotalCount, result.TotalPage });
        });
        group.MapGet("/{trackId}/playback", async (string bookId, string unitId, string trackId,
            ITextbookAudioPlaybackOperationFactory operations, CancellationToken cancellationToken) =>
        {
            if (!Valid(bookId, unitId, trackId)) return VocabularyHttpResponse.BadRequest("An ID is required.");
            using var operation = await operations.AcquireAsync(cancellationToken);
            if (!operation.Enabled) return Error("Audio storage is disabled.");
            try
            {
                var result = await operation.Playback.GetPublicAsync(bookId, unitId, trackId, cancellationToken);
                return VocabularyHttpResponse.Ok(new TextbookAudioPlaybackDto(result.TrackId, result.Link.Url, result.Link.ExpiresAt));
            }
            catch (AudioPlaybackUnavailableException) { return Error("Audio storage is unavailable."); }
        });
    }

    private static bool Valid(params string[] ids) => ids.All(id => !string.IsNullOrWhiteSpace(id));
    private static bool TryPage(HttpRequest request, string key, out int number)
    {
        number = 0;
        return !request.Query.TryGetValue(key, out var value) || value.Count == 1 &&
            int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
    }
    private static IResult Error(string message) => Results.Json(new { success = false, message }, statusCode: 503);
}
