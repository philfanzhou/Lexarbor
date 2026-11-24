using System.Globalization;
using System.Text;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Microsoft.AspNetCore.Http;

namespace Lexarbor.Learning.Service;

public static partial class VocabularyNotebookHttpEndpoints
{
    private static async Task<IResult> Summary(Guid studentId, VocabularyNotebookService service, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await service.SummaryAsync(studentId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = summary });
        }
        catch (Exception error) when (IsMapped(error))
        {
            return MapError(error);
        }
    }

    private static async Task<IResult> Query(Guid studentId, HttpRequest request, VocabularyNotebookService service,
        CancellationToken cancellationToken)
    {
        var query = request.Query;
        var keyword = query["keyword"].ToString().Trim();
        if (query["keyword"].Count > 1 || keyword.EnumerateRunes().Count() > 100
            || !TryFilter(query, "teacher", out var teacher) || !TryFilter(query, "recent", out var recent)
            || !TryFilter(query, "rejected", out var rejected) || !TryPage(query, out var page)
            || (keyword.Length == 0 && !teacher && !recent && !rejected))
            return Error(400, "VOCAB_NOTEBOOK_QUERY_INVALID", "Supply a keyword of up to 100 characters or a true filter, valid booleans and a positive page.");

        try
        {
            var result = await service.QueryAsync(studentId, new(keyword, teacher, recent, rejected, page), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new { success = true, data = new { result.Total, result.Page, pageSize = VocabNotebookQuery.PageSize, items = result.Items.Select(ToDto) } });
        }
        catch (Exception error) when (IsMapped(error))
        {
            return MapError(error);
        }
    }

    private static bool TryFilter(IQueryCollection query, string name, out bool value)
    {
        value = false;
        return !query.TryGetValue(name, out var input) || (input.Count == 1 && bool.TryParse(input[0], out value));
    }

    private static bool TryPage(IQueryCollection query, out int page)
    {
        page = 1;
        if (!query.TryGetValue("page", out var input)) return true;
        if (input.Count != 1 || input[0] is not string digits || digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return false;
        // Any positive integer beyond Int32 also clamps to the last available page.
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out page)) page = int.MaxValue;
        return page > 0;
    }

    private sealed record MembershipRequest(List<CardKey?>? Keys);

    private static async Task<IResult> Membership(Guid studentId, HttpRequest request, VocabularyNotebookService service,
        CancellationToken cancellationToken)
    {
        static IResult Invalid() => Error(400, "VOCAB_NOTEBOOK_MEMBERSHIP_INVALID", "Supply a keys array of up to 100 nonblank wordKey and meaningKey pairs.");
        if (!request.HasJsonContentType()) return Invalid();
        try
        {
            var body = await request.ReadFromJsonAsync<MembershipRequest>(cancellationToken).ConfigureAwait(false);
            if (body?.Keys is not { Count: <= 100 } keys) return Invalid();
            var distinct = new HashSet<CardKey>();
            foreach (var key in keys)
            {
                if (key is null || string.IsNullOrWhiteSpace(key.WordKey) || string.IsNullOrWhiteSpace(key.MeaningKey)) return Invalid();
                // PostgreSQL text cannot contain NUL, so such a pair can never be included.
                if (!key.WordKey.Contains('\0') && !key.MeaningKey.Contains('\0')) distinct.Add(key);
            }
            var included = await service.MembershipAsync(studentId, distinct, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = new { included } });
        }
        catch (JsonException)
        {
            return Invalid();
        }
        catch (Exception error) when (IsMapped(error))
        {
            return MapError(error);
        }
    }
}
