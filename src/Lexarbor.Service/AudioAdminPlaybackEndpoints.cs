using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class AudioAdminPlaybackEndpoints
{
    public static void MapAudioAdminPlaybackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/vocabulary/{wordId}/audio/{locale}/playback", async (HttpContext context, string wordId,
            string locale, [FromQuery] string? meaningId, IAudioPlaybackOperationFactory operations, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var operation = await operations.AcquireAsync(cancellationToken);
            if (!operation.Enabled) return Error(503, "Audio storage is disabled.");
            if (locale is not ("en-GB" or "en-US")) return Error(400, "Audio locale must be en-GB or en-US.");
            if (meaningId != null && string.IsNullOrWhiteSpace(meaningId)) return Error(400, "Meaning ID is invalid.");
            try
            {
                var result = await operation.Admin.GetAsync(wordId, meaningId, AudioLocales.FromCode(locale), cancellationToken);
                var selection = result.Selection;
                return VocabularyHttpResponse.Ok(new AudioPlaybackDto(locale, selection.Status.ToString().ToLowerInvariant(),
                    selection.Source?.ToString().ToLowerInvariant(), selection.EffectiveBinding?.Asset.Id,
                    selection.EffectiveBinding?.SourceRevision, result.Link?.Url, result.Link?.ExpiresAt));
            }
            catch (AudioPlaybackUnavailableException)
            {
                return Error(503, "Audio storage is unavailable.");
            }
        }).RequireAuthorization(AdminEndpointAuthorization.PolicyName);
    }

    private static IResult Error(int status, string message)
        => Results.Json(new { success = false, message }, statusCode: status);
}
