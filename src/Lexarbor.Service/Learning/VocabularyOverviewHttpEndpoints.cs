using System.Globalization;
using Lexarbor.Learning.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Learning.Service;

public static class VocabularyOverviewHttpEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyOverviewHttpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/vocabulary/students/{studentId}/overview", (RequestDelegate)(async context =>
        {
            var result = await GetOverviewAsync(context.Request.RouteValues["studentId"]?.ToString() ?? string.Empty,
                context.Request, context.RequestServices.GetRequiredService<VocabularyOverviewService>(),
                context.RequestAborted).ConfigureAwait(false);
            await result.ExecuteAsync(context).ConfigureAwait(false);
        }));
        return app;
    }

    private static async Task<IResult> GetOverviewAsync(string studentId, HttpRequest request,
        VocabularyOverviewService service, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(studentId, out var id) || id == Guid.Empty)
            return Error(400, "VOCAB_STUDENT_ID_INVALID", "A non-empty student id is required.");
        var offset = 0;
        if (request.Query.TryGetValue("tzOffsetMinutes", out var values) &&
            (values.Count != 1 || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
             || offset is < -720 or > 840))
            return Error(400, "VOCAB_TIMEZONE_INVALID", "Send one timezone offset between -720 and 840 minutes.");
        try
        {
            var overview = await service.GetAsync(id, offset, cancellationToken).ConfigureAwait(false);
            if (overview is null) return Error(404, "VOCAB_STUDENT_NOT_FOUND", "Student was not found.");
            return Results.Json(VocabularyOverviewResponse.From(overview),
                VocabularyOverviewJsonContext.Default.VocabularyOverviewResponse);
        }
        catch (VocabCatalogUnavailableException)
        {
            return Error(503, "VOCAB_CATALOG_UNAVAILABLE", "The vocabulary catalog is unavailable.");
        }
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { success = false, code, message }, statusCode: status);
}
