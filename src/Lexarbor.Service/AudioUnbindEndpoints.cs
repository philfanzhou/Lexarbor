using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class AudioUnbindEndpoints
{
    public static void MapAudioUnbind(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/vocabulary/{wordId}/audio/{locale}", async (HttpContext context, string wordId,
            string locale, [FromQuery] string? meaningId, AudioUnbindService service,
            IAudioAdminOperationFactory operations, CancellationToken cancellationToken) =>
        {
            using var operation = await operations.AcquireAsync(cancellationToken);
            if (!operation.Enabled) return ApiResponse.Failure(503, "Audio storage is disabled.");
            if (locale is not ("en-GB" or "en-US")) return ApiResponse.Failure(400, "Audio locale must be en-GB or en-US.");
            if (meaningId != null && string.IsNullOrWhiteSpace(meaningId)) return ApiResponse.Failure(400, "Meaning ID is invalid.");
            var values = context.Request.Headers.IfMatch;
            // A single strong entity tag identifies the own binding. Wildcards, weak tags and lists cannot authorize a delete.
            var value = values.Count == 1 ? values[0] : null;
            if (value is not { Length: 38 } || value[0] != '\"' || value[^1] != '\"'
                || !Guid.TryParseExact(value[1..^1], "D", out var asset) || asset.ToString() != value[1..^1])
                return ApiResponse.Failure(400, "If-Match must contain one quoted current audio asset UUID.");
            var result = await service.UnbindAsync(wordId, meaningId, AudioLocales.FromCode(locale), asset.ToString(), cancellationToken);
            return result switch
            {
                AudioUnbindDisposition.Removed => ApiResponse.Ok(new { removed = true }),
                AudioUnbindDisposition.NotFound => ApiResponse.Failure(404, "Audio binding was not found."),
                _ => ApiResponse.Failure(409, "Audio binding conflicts with If-Match.")
            };
        }).RequireAuthorization(AdminEndpointAuthorization.PolicyName);
    }
}
