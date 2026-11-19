using System.Globalization;
using Lexarbor.Learning.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Learning.Service;

/// <summary>Read-only check-in calendar for trusted Portal callers (ADR-0002/ADR-0019); no learning writes.</summary>
public static class VocabularyCheckInHttpEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyCheckInHttpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/vocabulary/students/{studentId}/check-ins", (RequestDelegate)(async context =>
        {
            var result = await GetAsync(context.Request.RouteValues["studentId"]?.ToString() ?? string.Empty,
                context.Request, context.RequestServices.GetRequiredService<VocabularyCheckInService>(),
                context.RequestAborted).ConfigureAwait(false);
            await result.ExecuteAsync(context).ConfigureAwait(false);
        }));
        return app;
    }

    private static async Task<IResult> GetAsync(string studentId, HttpRequest request,
        VocabularyCheckInService service, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(studentId, out var id) || id == Guid.Empty)
            return Error(400, "VOCAB_STUDENT_ID_INVALID", "A non-empty student id is required.");
        var offset = 0;
        if (request.Query.TryGetValue("tzOffsetMinutes", out var values) &&
            (values.Count != 1 || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
             || offset is < -720 or > 840))
            return Error(400, "VOCAB_TIMEZONE_INVALID", "Send one timezone offset between -720 and 840 minutes.");
        if (!TryDate(request, "from", out var from) || !TryDate(request, "to", out var to)
            || to < from || to.DayNumber - from.DayNumber + 1 > VocabularyCheckInService.MaxRangeDays)
            return Error(400, "VOCAB_CHECKIN_RANGE_INVALID", "Send from and to dates (yyyy-MM-dd) covering at most 62 days.");
        var checkIns = await service.GetAsync(id, from, to, offset, cancellationToken).ConfigureAwait(false);
        return checkIns is null
            ? Error(404, "VOCAB_STUDENT_NOT_FOUND", "Student was not found.")
            : Results.Json(VocabularyCheckInResponse.From(checkIns), VocabularyCheckInJsonContext.Default.VocabularyCheckInResponse);
    }

    private static bool TryDate(HttpRequest request, string name, out DateOnly date)
    {
        date = default;
        return request.Query.TryGetValue(name, out var values) && values.Count == 1
            && DateOnly.TryParseExact(values[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { success = false, code, message }, statusCode: status);
}
