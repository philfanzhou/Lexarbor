using System.Text.Json;
using Lexarbor.Domain.Services;
using Microsoft.AspNetCore.Http;

namespace Lexarbor.Service;

public static partial class VocabularyHttpEndpoints
{
    private const int MaxLookupItems = 100;

    private static async Task<IResult> LookupMeanings(HttpRequest request, VocabularyPublicQueryService query, CancellationToken token)
    {
        var (input, error) = await ReadLookupRequestAsync<MeaningLookupRequest>(request, token);
        if (error != null) return error;

        if (input?.Words is not { Length: >= 1 and <= MaxLookupItems } words || words.Any(string.IsNullOrWhiteSpace))
            return VocabularyHttpResponse.BadRequest($"Supply 1 to {MaxLookupItems} nonblank spellings.");

        var items = await query.LookupMeaningsAsync(words, token);
        return VocabularyHttpResponse.Ok(new { items });
    }

    private static async Task<(T? Input, IResult? Error)> ReadLookupRequestAsync<T>(HttpRequest request, CancellationToken token)
        where T : class
    {
        if (request.ContentLength > MaxBatchRequestBytes) return (null, LookupRequestTooLarge());

        // A chunked body has no length header, and some hosts have no size-limit feature.
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer, token)) != 0)
            {
                if (body.Length + read > MaxBatchRequestBytes) return (null, LookupRequestTooLarge());
                body.Write(buffer, 0, read);
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, LookupRequestTooLarge());
        }

        try
        {
            var input = request.HasJsonContentType()
                ? JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), JsonSerializerOptions.Web)
                : null;
            return (input, null);
        }
        catch (JsonException)
        {
            return (null, VocabularyHttpResponse.BadRequest("The request body is not valid JSON."));
        }
    }

    private static IResult LookupRequestTooLarge() => Results.Json(new { success = false, message = "The request body is too large." },
        statusCode: StatusCodes.Status413PayloadTooLarge);

    private sealed record MeaningLookupRequest(string[]? Words);
}
