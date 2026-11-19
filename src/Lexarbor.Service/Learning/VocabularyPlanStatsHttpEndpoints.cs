using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Lexarbor.Learning.Domain.Services;

namespace Lexarbor.Learning.Service;

public static class VocabularyPlanStatsHttpEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyPlanStatsHttpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/vocabulary/plans/stats", (RequestDelegate)(async context =>
        {
            var result = await GetAsync(context, context.RequestServices.GetRequiredService<VocabularyPlanStatsService>()).ConfigureAwait(false);
            await result.ExecuteAsync(context).ConfigureAwait(false);
        }));
        return app;
    }
    private static async Task<IResult> GetAsync(HttpContext context, VocabularyPlanStatsService service)
    {
        List<Guid> ids = [];
        var offset = 0;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("planIds", out var array)
                || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 100)
                return Error(400, "VOCAB_PLAN_IDS_INVALID", "Send between one and 100 non-empty plan ids.");
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id) || id == Guid.Empty)
                    return Error(400, "VOCAB_PLAN_IDS_INVALID", "Send between one and 100 non-empty plan ids.");
                ids.Add(id);
            }
            if (root.TryGetProperty("tzOffsetMinutes", out var timezone) &&
                (timezone.ValueKind != JsonValueKind.Number || !timezone.TryGetInt32(out offset) || offset is < -720 or > 840))
                return Error(400, "VOCAB_TIMEZONE_INVALID", "Send a timezone offset between -720 and 840 minutes.");
        }
        catch (JsonException) { return Error(400, "VOCAB_PLAN_IDS_INVALID", "Send a valid JSON request."); }
        try
        {
            var stats = await service.GetAsync(ids, offset, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new VocabularyPlanStatsResponse(true, stats), VocabularyPlanStatsJsonContext.Default.VocabularyPlanStatsResponse);
        }
        catch (VocabularyStatisticsUnavailableException)
        { return Error(503, "VOCAB_STATISTICS_UNAVAILABLE", "Vocabulary statistics are temporarily unavailable."); }
        catch (VocabCatalogUnavailableException)
        { return Error(503, "VOCAB_CATALOG_UNAVAILABLE", "The vocabulary catalog is unavailable."); }
    }
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { success = false, code, message }, statusCode: status);
}
