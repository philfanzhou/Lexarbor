using System.Net.Http.Json;
using Lexarbor.Learning.Domain.Services;

namespace Lexarbor.Learning.Service;

public sealed partial class CatalogClient : IVocabularyPhotoCatalog
{
    private const int MaxMeaningLookupItems = 100;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<VocabCatalogMeaning>>> LookupMeaningsAsync(
        IReadOnlyList<string> words, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, IReadOnlyList<VocabCatalogMeaning>>(StringComparer.Ordinal);
        foreach (var batch in words.Chunk(MaxMeaningLookupItems))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, CatalogUrl("api/vocabulary/meanings/lookup"))
            {
                Content = JsonContent.Create(new { words = batch }),
            };
            var data = await ReadCatalogAsync<MeaningLookupBatch>(request, allowMissingWord: false, cancellationToken).ConfigureAwait(false);
            if (data?.Items is not { } items || items.Count != batch.Length) throw new VocabCatalogUnavailableException();

            for (var index = 0; index < batch.Length; index++)
            {
                var word = batch[index];
                var item = items[index];
                if (item?.Word != word || item.Meanings is null) throw new VocabCatalogUnavailableException();
                var wordKey = VocabularyPhotoEntries.SpellingKey(word);
                var meanings = new List<VocabCatalogMeaning>(item.Meanings.Count);
                foreach (var meaning in item.Meanings)
                {
                    if (meaning is null || meaning.WordKey != wordKey || string.IsNullOrWhiteSpace(meaning.WordId)
                        || string.IsNullOrWhiteSpace(meaning.Word) || string.IsNullOrWhiteSpace(meaning.BookId)
                        || string.IsNullOrWhiteSpace(meaning.BookName) || string.IsNullOrWhiteSpace(meaning.MeaningId)
                        || string.IsNullOrWhiteSpace(meaning.Meaning) || string.IsNullOrWhiteSpace(meaning.MeaningKey))
                        throw new VocabCatalogUnavailableException();
                    meanings.Add(meaning);
                }

                results.Add(word, meanings);
            }
        }

        return results;
    }

    private sealed record MeaningLookupBatch(List<MeaningLookupItem?>? Items);
    private sealed record MeaningLookupItem(string? Word, List<VocabCatalogMeaning?>? Meanings);
}
