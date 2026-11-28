using System.Globalization;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Learning.Service;

public static class VocabularyErrorProneEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyErrorProne(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/vocabulary/students/{studentId}/error-prone", (RequestDelegate)(async context =>
        {
            var result = await GetAsync(context).ConfigureAwait(false);
            await result.ExecuteAsync(context).ConfigureAwait(false);
        }));
        return app;
    }
    private static async Task<IResult> GetAsync(HttpContext context)
    {
        if (!Guid.TryParse(context.Request.RouteValues["studentId"]?.ToString(), out var id) || id == Guid.Empty)
            return ApiResponse.Failure(400, "VOCAB_STUDENT_ID_INVALID", "A non-empty student id is required.");
        if (!ReadInt(context.Request, "page", 1, out var page) || !ReadInt(context.Request, "size", 20, out var size) ||
            !VocabularyErrorProneService.ValidPaging(page, size))
            return ApiResponse.Failure(400, "VOCAB_PAGING_INVALID", "Send one valid page and size between 1 and 100.");
        try
        {
            var data = await context.RequestServices.GetRequiredService<VocabularyErrorProneService>()
                .GetAsync(id, page, size, context.RequestAborted).ConfigureAwait(false);
            return data is null ? ApiResponse.Failure(404, "VOCAB_STUDENT_NOT_FOUND", "Student was not found.") :
                Results.Json(new VocabularyErrorProneResponse(true, data), VocabularyErrorProneJsonContext.Default.VocabularyErrorProneResponse);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return ApiResponse.Failure(500, "VOCAB_STATISTICS_UNAVAILABLE", "Vocabulary statistics are unavailable.");
        }
    }
    private static bool ReadInt(HttpRequest request, string key, int defaultValue, out int value)
    {
        value = defaultValue;
        return !request.Query.TryGetValue(key, out var values) ||
            (values.Count == 1 && int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out value));
    }
}
