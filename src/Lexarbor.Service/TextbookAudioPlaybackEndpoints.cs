using Lexarbor.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class TextbookAudioPlaybackEndpoints
{
    public static void MapTextbookAudioPlaybackEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks/{trackId}/playback",
            async (string bookId, string unitId, string trackId, ITextbookAudioPlaybackOperationFactory operations,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(unitId) || string.IsNullOrWhiteSpace(trackId))
                    return VocabularyHttpResponse.BadRequest("An ID is required.");
                using var operation = await operations.AcquireAsync(cancellationToken);
                if (!operation.Enabled) return Error("Audio storage is disabled.");
                try
                {
                    var result = await operation.Playback.GetAdminAsync(bookId.Trim(), unitId.Trim(), trackId.Trim(), cancellationToken);
                    return VocabularyHttpResponse.Ok(new { result.TrackId, result.Link.Url, result.Link.ExpiresAt });
                }
                catch (AudioPlaybackUnavailableException) { return Error("Audio storage is unavailable."); }
            }).RequireAuthorization(AdminEndpointAuthorization.PolicyName);
    }

    private static IResult Error(string message) => Results.Json(new { success = false, message }, statusCode: 503);
}
