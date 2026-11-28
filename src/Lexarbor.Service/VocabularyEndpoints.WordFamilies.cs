using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.AspNetCore.Http;

namespace Lexarbor.Service;

public static partial class VocabularyEndpoints
{
    private static async Task<IResult> GetWordFamily(string wordId, string? meaning, string? partOfSpeech,
        VocabularyPublicQueryService query, CancellationToken token)
    {
        var input = new VocabularyFamilyQuery(wordId, Meaning: meaning, PartOfSpeech: partOfSpeech);
        if (!IsValidFamilyQuery(input))
            return ApiResponse.BadRequest("The word ID and any supplied meaning or part of speech must be nonblank.");

        var result = (await query.LookupRelationsAsync([input], token))[0];
        return result.Found
            ? ApiResponse.Ok(new { result.WordId, result.Items })
            : ApiResponse.NotFound("Vocabulary word was not found.");
    }

    private static async Task<IResult> LookupWordFamilies(HttpRequest request, VocabularyPublicQueryService query, CancellationToken token)
    {
        var (input, error) = await ReadLookupRequestAsync<WordFamilyLookupRequest>(request, token);
        if (error != null) return error;

        var queries = input switch
        {
            { WordIds: { Length: >= 1 and <= MaxLookupItems } ids, Items: null } => ids.Select(id => new VocabularyFamilyQuery(id)).ToArray(),
            { WordIds: null, Items: { } entries } => entries,
            _ => null
        };
        if (queries is not { Length: >= 1 and <= MaxLookupItems } || queries.Any(entry => entry == null || !IsValidFamilyQuery(entry)))
            return ApiResponse.BadRequest(
                $"Supply 1 to {MaxLookupItems} word IDs or lookup entries. Each entry must have a word ID or a spelling, with nonblank meaning and part of speech when supplied.");

        var items = await query.LookupRelationsAsync(queries, token);
        return ApiResponse.Ok(new { items });
    }

    private static bool IsValidFamilyQuery(VocabularyFamilyQuery input) =>
        (input.WordId == null ? !string.IsNullOrWhiteSpace(input.Word) : input.Word == null && !string.IsNullOrWhiteSpace(input.WordId))
        && (input.Meaning == null || !string.IsNullOrWhiteSpace(input.Meaning))
        && (input.PartOfSpeech == null || !string.IsNullOrWhiteSpace(input.PartOfSpeech));

    private sealed record WordFamilyLookupRequest(string[]? WordIds, VocabularyFamilyQuery[]? Items);
}
