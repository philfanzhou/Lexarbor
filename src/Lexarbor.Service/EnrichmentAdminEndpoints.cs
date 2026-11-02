using System.Text.Json;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class EnrichmentAdminEndpoints
{
    public static void MapEnrichmentAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/enrichment").RequireAuthorization(AdminEndpointAuthorization.PolicyName);
        group.MapGet("/tasks", async ([AsParameters] EnrichmentAdminQuery query, IEnrichmentAdminRepository repository,
            IEnrichmentAdminConfigurationProvider configuration, TimeProvider clock, CancellationToken token) =>
        {
            if (!query.Valid()) return Error(400, "Invalid enrichment query.");
            return VocabularyHttpResponse.Ok(await repository.ListAsync(query.Filter(), await configuration.CaptureAsync(token), Now(clock), token));
        });
        group.MapGet("/summary", async ([AsParameters] EnrichmentAdminQuery query, IEnrichmentAdminRepository repository,
            IEnrichmentAdminConfigurationProvider configuration, TimeProvider clock, CancellationToken token) =>
        {
            if (!query.Valid()) return Error(400, "Invalid enrichment query.");
            return VocabularyHttpResponse.Ok(await repository.SummaryAsync(query.Filter(), await configuration.CaptureAsync(token), Now(clock), token));
        });
        group.MapGet("/tasks/{id}", async (string id, [FromQuery] int? historyPage, [FromQuery] int? historySize,
            IEnrichmentAdminRepository repository, IEnrichmentAdminConfigurationProvider configuration, TimeProvider clock, CancellationToken token) =>
        {
            if (historyPage is < 1 or > 1000000 || historySize is < 1 or > 100) return Error(400, "Invalid enrichment history page.");
            var detail = await repository.DetailAsync(id, historyPage ?? 1, historySize ?? 25, await configuration.CaptureAsync(token), Now(clock), token);
            return detail == null ? Error(404, "Enrichment task was not found.") : VocabularyHttpResponse.Ok(detail);
        });
        group.MapPost("/tasks/{id}/retry", RetryAsync);
    }
    private static async Task<IResult> RetryAsync(string id, HttpContext context, IEnrichmentAdminRetryRepository repository,
        IEnrichmentAdminConfigurationProvider configuration, IEnrichmentChangeNotifier notifier, TimeProvider clock)
    {
        var token = context.RequestAborted;
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(bytes, token);
            if (count == 0) break;
            if (buffer.Length + count > 1024) return Error(413, "Retry request is too large.");
            buffer.Write(bytes, 0, count);
        }
        int expectedRound = 0; string? retryKey = null;
        try
        {
            using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 2 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Error(400, "Invalid retry request.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return Error(400, "Invalid retry request.");
                if (property.Name == "expectedRound" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var round)) expectedRound = round;
                else if (property.Name == "retryKey" && property.Value.ValueKind == JsonValueKind.String) retryKey = property.Value.GetString();
                else return Error(400, "Invalid retry request.");
            }
            if (expectedRound < 1 || expectedRound == int.MaxValue || !Guid.TryParseExact(retryKey, "D", out var key) || key.ToString() != retryKey)
                return Error(400, "Retry requires expectedRound and a canonical UUID retryKey.");
        }
        catch (JsonException) { return Error(400, "Invalid retry request."); }
        var result = await repository.RetryAsync(id, expectedRound, retryKey!, await configuration.CaptureAsync(token), Now(clock), token);
        if (result.Status == EnrichmentRetryStatus.NotFound) return Error(404, "Enrichment task was not found.");
        if (result.Status == EnrichmentRetryStatus.Conflict) return Error(409, "The task is no longer the expected failed, missing step. Reload its state.");
        // The committed durable queue remains authoritative if notification fails.
        try { notifier.Notify([]); } catch (Exception) { }
        return VocabularyHttpResponse.Ok(new { targetId = result.TargetId, round = result.Round, replayed = result.Status == EnrichmentRetryStatus.Replayed });
    }
    private static long Now(TimeProvider clock) => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static IResult Error(int status, string message) => Results.Json(new { success = false, message }, statusCode: status);
}

public sealed class EnrichmentAdminQuery
{
    [FromQuery] public string? WordId { get; set; }
    [FromQuery] public string? MeaningId { get; set; }
    [FromQuery] public string? Locale { get; set; }
    [FromQuery] public string? State { get; set; }
    [FromQuery] public string? Step { get; set; }
    [FromQuery] public int? Page { get; set; }
    [FromQuery] public int? Size { get; set; }
    internal bool Valid() => Page is not (< 1 or > 1000000) && Size is not (< 1 or > 100)
        && (WordId == null || WordId.Length is > 0 and <= 128 && !WordId.Any(char.IsControl))
        && (MeaningId == null || MeaningId.Length is > 0 and <= 128 && !MeaningId.Any(char.IsControl))
        && Locale is null or "en-GB" or "en-US"
        && State is null or "Pending" or "Running" or "Waiting" or "Succeeded" or "Failed" or "Cancelled"
        && Step is null or "Prepare" or "Fields" or "Audio";
    internal EnrichmentAdminFilter Filter() => new(WordId, MeaningId, Locale, State, Step, Page ?? 1, Size ?? 25);
}
