using System.Globalization;
using Lexarbor.Learning.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Learning.Service;

public static class VocabularyWordListHttpEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyWordListHttpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/vocabulary/students/{studentId}/words", (RequestDelegate)(async context =>
        {
            var result = await GetAsync(context).ConfigureAwait(false);
            await result.ExecuteAsync(context).ConfigureAwait(false);
        }));
        return app;
    }

    private static async Task<IResult> GetAsync(HttpContext context)
    {
        if (!Guid.TryParse(context.Request.RouteValues["studentId"]?.ToString(), out var id) || id == Guid.Empty)
            return Error(400, "VOCAB_STUDENT_ID_INVALID", "A non-empty student id is required.");
        if (!ReadText(context.Request, "view", out var view) || !VocabularyWordListService.ValidView(view))
            return Error(400, "VOCAB_WORD_VIEW_INVALID", "Send one view: mastered or due.");
        if (!ReadText(context.Request, "q", out var query) || !VocabularyWordListService.ValidQuery(query))
            return Error(400, "VOCAB_WORD_QUERY_INVALID", "Send at most one search text of up to 50 characters.");
        if (!ReadInt(context.Request, "page", 1, out var page) || !ReadInt(context.Request, "size", 20, out var size) ||
            !VocabularyErrorProneService.ValidPaging(page, size))
            return Error(400, "VOCAB_PAGING_INVALID", "Send one valid page and size between 1 and 100.");
        try
        {
            var data = await context.RequestServices.GetRequiredService<VocabularyWordListService>()
                .GetAsync(id, view!, query, page, size, context.RequestAborted).ConfigureAwait(false);
            return data is null ? Error(404, "VOCAB_STUDENT_NOT_FOUND", "Student was not found.") :
                Results.Json(new VocabularyWordListResponse(true, data), VocabularyWordListJsonContext.Default.VocabularyWordListResponse);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Error(500, "VOCAB_STATISTICS_UNAVAILABLE", "Vocabulary statistics are unavailable.");
        }
    }

    // Absent is allowed (null); a repeated key is rejected even with equal values.
    private static bool ReadText(HttpRequest request, string key, out string? value)
    {
        value = null;
        if (!request.Query.TryGetValue(key, out var values)) return true;
        if (values.Count != 1) return false;
        value = values[0];
        return true;
    }

    private static bool ReadInt(HttpRequest request, string key, int defaultValue, out int value)
    {
        value = defaultValue;
        return !request.Query.TryGetValue(key, out var values) ||
            (values.Count == 1 && int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out value));
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { success = false, code, message }, statusCode: status);
}
