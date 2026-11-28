using System.Net.Http.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;

namespace Lexarbor.Learning.Service;

public sealed partial class CatalogClient : IVocabularyFamilyCatalog
{
    public async Task<IReadOnlyList<VocabularyFamilyRelation>> GetFamilyAsync(string wordId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl($"api/vocabulary/{EscapeSegment(wordId)}/relations"));
        var data = await ReadCatalogAsync<FamilyResult>(request, allowMissingWord: true, cancellationToken).ConfigureAwait(false);
        return data is null ? [] : ReadRelations(wordId, data);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<VocabularyFamilyRelation>>> LookupFamiliesAsync(
        IReadOnlyList<string> wordIds, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CatalogUrl("api/vocabulary/relations/lookup"))
        {
            Content = JsonContent.Create(new { wordIds }),
        };
        var data = await ReadCatalogAsync<FamilyBatch>(request, allowMissingWord: false, cancellationToken).ConfigureAwait(false);
        if (data?.Items is not { } items || items.Count != wordIds.Count) throw new VocabCatalogUnavailableException();

        var results = new Dictionary<string, IReadOnlyList<VocabularyFamilyRelation>>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item?.Found is null || item.WordId != wordIds[i] || item.Items is null || (!item.Found.Value && item.Items.Count != 0))
                throw new VocabCatalogUnavailableException();
            results.Add(wordIds[i], ReadRelations(wordIds[i], item));
        }
        return results;
    }

    private static IReadOnlyList<VocabularyFamilyRelation> ReadRelations(string wordId, FamilyResult data)
    {
        if (data.WordId != wordId || data.Items is null) throw new VocabCatalogUnavailableException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var relations = new List<VocabularyFamilyRelation>(data.Items.Count);
        foreach (var item in data.Items)
        {
            if (item?.RelationId is not { } id || string.IsNullOrWhiteSpace(id) || !seen.Add(id)
                || !VocabularyFamilyRelation.ValidType(item.Type) || item.Direction is not ("incoming" or "outgoing")
                || item.Word is not { } word || string.IsNullOrWhiteSpace(word.WordId) || string.IsNullOrWhiteSpace(word.Word)
                || string.IsNullOrWhiteSpace(word.NormalizedWord) || item.Meanings is null)
                throw new VocabCatalogUnavailableException();
            var meanings = item.Meanings.Select(meaning =>
            {
                if (meaning is null || string.IsNullOrWhiteSpace(meaning.BookId) || string.IsNullOrWhiteSpace(meaning.BookName)
                    || string.IsNullOrWhiteSpace(meaning.MeaningId) || string.IsNullOrWhiteSpace(meaning.Meaning)
                    || string.IsNullOrWhiteSpace(meaning.MeaningKey))
                    throw new VocabCatalogUnavailableException();
                return new VocabCatalogMeaning(word.WordId!, word.Word!, word.NormalizedWord!, null, null, meaning.BookId,
                    meaning.BookName, meaning.MeaningId, meaning.PartOfSpeech, meaning.Meaning, meaning.MeaningKey);
            }).ToArray();
            var otherMeanings = meanings.Select(meaning => new VocabularyCatalogReference(meaning.WordId, meaning.BookId, meaning.MeaningId)).ToHashSet();
            var pairs = new List<VocabularyFamilyMeaningPair>();
            foreach (var pair in item.MeaningPairs ?? [])
            {
                if (pair?.Source is not { } source || pair.Target is not { } target
                    || !ValidReference(source) || !ValidReference(target))
                    throw new VocabCatalogUnavailableException();
                var outgoing = item.Direction == "outgoing";
                if ((outgoing ? source : target).WordId != wordId || !otherMeanings.Contains(outgoing ? target : source))
                    throw new VocabCatalogUnavailableException();
                pairs.Add(new(source, target));
            }
            relations.Add(new(id, item.Type!, item.Direction, item.UsageNote, word.Word, word.NormalizedWord, meanings, pairs)
            {
                QueryWordId = wordId
            });
        }
        return relations;
    }

    private static bool ValidReference(VocabularyCatalogReference reference) =>
        !string.IsNullOrWhiteSpace(reference.WordId) && !string.IsNullOrWhiteSpace(reference.BookId)
        && !string.IsNullOrWhiteSpace(reference.MeaningId);

    private sealed record FamilyBatch(List<FamilyResult?>? Items);
    private sealed record FamilyResult(string? WordId, bool? Found, List<FamilyRelation?>? Items);
    private sealed record FamilyRelation(string? RelationId, string? Type, string? Direction, string? UsageNote,
        FamilyWord? Word, List<FamilyMeaning?>? Meanings, List<VocabularyFamilyMeaningPair?>? MeaningPairs);
    private sealed record FamilyWord(string? WordId, string? Word, string? NormalizedWord);
    private sealed record FamilyMeaning(string? BookId, string? BookName, string? MeaningId, string? PartOfSpeech,
        string? Meaning, string? MeaningKey);
}
