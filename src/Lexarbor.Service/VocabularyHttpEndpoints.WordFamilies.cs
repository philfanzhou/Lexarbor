using System.Text.Json;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.AspNetCore.Http;

namespace Lexarbor.Service;

public static partial class VocabularyHttpEndpoints
{
    private const int MaxFamilyLookupItems = 100;

    private static async Task<IResult> GetWordFamily(string wordId, string? meaning, string? partOfSpeech,
        VocabularyPublicQueryService query, CancellationToken token)
    {
        var input = new VocabularyFamilyQuery(wordId, Meaning: meaning, PartOfSpeech: partOfSpeech);
        if (!IsValidFamilyQuery(input))
            return VocabularyHttpResponse.BadRequest("The word ID and any supplied meaning or part of speech must be nonblank.");

        var result = (await query.LookupRelationsAsync([input], token))[0];
        return result.Found
            ? VocabularyHttpResponse.Ok(new { result.WordId, result.Items })
            : VocabularyHttpResponse.NotFound("Vocabulary word was not found.");
    }

    private static async Task<IResult> LookupWordFamilies(HttpRequest request, VocabularyPublicQueryService query, CancellationToken token)
    {
        if (request.ContentLength > MaxBatchRequestBytes) return FamilyRequestTooLarge();

        // A chunked body has no length header, and some hosts have no size-limit feature.
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer, token)) != 0)
            {
                if (body.Length + read > MaxBatchRequestBytes) return FamilyRequestTooLarge();
                body.Write(buffer, 0, read);
            }
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return FamilyRequestTooLarge();
        }

        WordFamilyLookupRequest? input;
        try
        {
            input = request.HasJsonContentType()
                ? JsonSerializer.Deserialize<WordFamilyLookupRequest>(body.GetBuffer().AsSpan(0, (int)body.Length), JsonSerializerOptions.Web)
                : null;
        }
        catch (JsonException)
        {
            return VocabularyHttpResponse.BadRequest("The request body is not valid JSON.");
        }

        var queries = input switch
        {
            { WordIds: { Length: >= 1 and <= MaxFamilyLookupItems } ids, Items: null } => ids.Select(id => new VocabularyFamilyQuery(id)).ToArray(),
            { WordIds: null, Items: { } entries } => entries,
            _ => null
        };
        if (queries is not { Length: >= 1 and <= MaxFamilyLookupItems } || queries.Any(entry => entry == null || !IsValidFamilyQuery(entry)))
            return VocabularyHttpResponse.BadRequest(
                $"Supply 1 to {MaxFamilyLookupItems} word IDs or lookup entries. Each entry must have a word ID or a spelling, with nonblank meaning and part of speech when supplied.");

        var items = await query.LookupRelationsAsync(queries, token);
        return VocabularyHttpResponse.Ok(new { items });
    }

    private static bool IsValidFamilyQuery(VocabularyFamilyQuery input) =>
        (input.WordId == null ? !string.IsNullOrWhiteSpace(input.Word) : input.Word == null && !string.IsNullOrWhiteSpace(input.WordId))
        && (input.Meaning == null || !string.IsNullOrWhiteSpace(input.Meaning))
        && (input.PartOfSpeech == null || !string.IsNullOrWhiteSpace(input.PartOfSpeech));

    private static IResult FamilyRequestTooLarge() => Results.Json(new { success = false, message = "The request body is too large." },
        statusCode: StatusCodes.Status413PayloadTooLarge);

    private sealed record WordFamilyLookupRequest(string[]? WordIds, VocabularyFamilyQuery[]? Items);
}
