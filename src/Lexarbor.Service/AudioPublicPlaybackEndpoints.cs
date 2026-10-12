using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class AudioPublicPlaybackEndpoints
{
    public static void MapAudioPublicPlaybackEndpoints(this IEndpointRouteBuilder app,
        string? publicApiRateLimitPolicy = null)
    {
        var endpoint = app.MapGet("/api/vocabulary/{wordId}/audio", async (HttpContext context, string wordId,
            [FromQuery] string? bookId, [FromQuery] string? meaningId, [FromQuery] string? locale,
            IAudioPlaybackOperationFactory operations, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (string.IsNullOrWhiteSpace(bookId)) return Error(400, "Book ID is required.");
            if (locale != null && locale is not ("en-GB" or "en-US")) return Error(400, "Audio locale must be en-GB or en-US.");
            if (meaningId != null && string.IsNullOrWhiteSpace(meaningId)) return Error(400, "Meaning ID is invalid.");
            using var operation = await operations.AcquireAsync(cancellationToken);
            if (!operation.Enabled) return Error(503, "Audio storage is disabled.");
            try
            {
                var results = await operation.Public.GetAsync(bookId.Trim(), wordId, meaningId?.Trim(),
                    locale == null ? null : AudioLocales.FromCode(locale), cancellationToken);
                return VocabularyHttpResponse.Ok(new
                {
                    items = results.Select(result => new AudioPlaybackDto(
                    result.Selection.Input.Locale.ToCode(), result.Selection.Status.ToString().ToLowerInvariant(),
                    result.Selection.Source?.ToString().ToLowerInvariant(), result.Selection.EffectiveBinding?.Asset.Id,
                    result.Selection.EffectiveBinding?.SourceRevision, result.Link?.Url, result.Link?.ExpiresAt)).ToList()
                });
            }
            catch (AudioPlaybackUnavailableException) { return Error(503, "Audio storage is unavailable."); }
        });
        if (!string.IsNullOrWhiteSpace(publicApiRateLimitPolicy)) endpoint.RequireRateLimiting(publicApiRateLimitPolicy);
    }
    private static IResult Error(int status, string message)
        => Results.Json(new { success = false, message }, statusCode: status);
}
