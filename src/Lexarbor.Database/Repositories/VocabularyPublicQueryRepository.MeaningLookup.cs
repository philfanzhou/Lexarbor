using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed partial class VocabularyPublicQueryRepository
{
    public async Task<IReadOnlyList<VocabularyMeaningLookup>> LookupMeaningsAsync(IReadOnlyList<string> words,
        CancellationToken cancellationToken)
    {
        var keys = words.Select(CatalogKeys.Of).ToArray();
        var distinctKeys = keys.Distinct(StringComparer.Ordinal).ToArray();
        var meanings = await (
            from word in context.Vocabularies.AsNoTracking()
            join meaning in context.VocabularyMeanings.AsNoTracking() on word.Id equals meaning.VocabularyId
            join book in context.VocabularyBooks.AsNoTracking() on meaning.BookId equals book.Id
            where book.Status && EF.Parameter(distinctKeys).Contains(word.NormalizedWord)
            orderby book.DisplayOrder, book.BookName, book.Id, word.Id, meaning.NormalizedPartOfSpeech, meaning.NormalizedMeaning, meaning.Id
            select new VocabularyLookupMeaning(word.Id, word.Word, word.NormalizedWord, book.Id, book.BookName,
                meaning.Id, meaning.PartOfSpeech, meaning.Meaning, meaning.NormalizedMeaning.ToLower()))
            .ToArrayAsync(cancellationToken);

        var byKey = meanings.GroupBy(item => item.WordKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group
                .Select(meaning => string.IsNullOrWhiteSpace(meaning.PartOfSpeech) ? meaning with { PartOfSpeech = null } : meaning)
                .ToArray(), StringComparer.Ordinal);
        return words.Select((word, index) => new VocabularyMeaningLookup(word, byKey.GetValueOrDefault(keys[index]) ?? [])).ToArray();
    }
}
