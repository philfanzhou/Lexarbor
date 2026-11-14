using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed partial class VocabularyPublicQueryRepository
{
    private static readonly Dictionary<string, int> FamilyTypeOrder = VocabularyRelationTypes.All
        .Select((type, index) => (type.Code, index)).ToDictionary(pair => pair.Code, pair => pair.index, StringComparer.Ordinal);

    public Task<IReadOnlyList<VocabularyFamilyLookup>> LookupRelationsAsync(IReadOnlyList<VocabularyFamilyQuery> queries,
        CancellationToken cancellationToken)
        => SnapshotAsync<IReadOnlyList<VocabularyFamilyLookup>>(async () =>
        {
            var normalized = queries.Select(query => new VocabularyFamilyQuery(query.WordId,
                query.Word == null ? null : CatalogKeys.Of(query.Word),
                query.Meaning == null ? null : CatalogKeys.Of(query.Meaning),
                query.PartOfSpeech == null ? null : CatalogKeys.Of(query.PartOfSpeech))).ToArray();
            var requestedIds = normalized.Select(query => query.WordId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            var spellings = normalized.Select(query => query.Word).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            var catalog = context.Vocabularies.AsNoTracking();
            var candidates = await SelectFamilyWords(catalog.Where(word => EF.Parameter(requestedIds).Contains(word.Id)
                    || EF.Parameter(spellings).Contains(word.NormalizedWord))).ToArrayAsync(cancellationToken);
            var words = candidates.ToDictionary(word => word.WordId, StringComparer.Ordinal);
            var wordsBySpelling = candidates.OrderBy(word => word.WordId, StringComparer.Ordinal)
                .ToLookup(word => word.NormalizedWord, StringComparer.Ordinal);

            var enabledMeanings = from meaning in context.VocabularyMeanings.AsNoTracking()
                                  join book in context.VocabularyBooks.AsNoTracking() on meaning.BookId equals book.Id
                                  where book.Status
                                  select new { Meaning = meaning, book.BookName, MeaningKey = meaning.NormalizedMeaning.ToLower() };
            var filters = normalized.Where(query => query.Meaning != null || query.PartOfSpeech != null).ToArray();
            var filteredIds = filters.SelectMany(query => CandidatesFor(query)).Select(word => word.WordId)
                .Distinct(StringComparer.Ordinal).ToArray();
            var matchingRows = filteredIds.Length == 0 ? [] : await enabledMeanings
                .Where(row => EF.Parameter(filteredIds).Contains(row.Meaning.VocabularyId))
                .Select(row => new { row.Meaning.VocabularyId, row.MeaningKey, row.Meaning.NormalizedPartOfSpeech })
                .ToArrayAsync(cancellationToken);
            var meaningsByCandidate = matchingRows.ToLookup(row => row.VocabularyId, StringComparer.Ordinal);
            var resolved = normalized.Select(query => CandidatesFor(query).FirstOrDefault(word =>
                (query.Meaning == null && query.PartOfSpeech == null) || meaningsByCandidate[word.WordId].Any(meaning =>
                    (query.Meaning == null || meaning.MeaningKey == query.Meaning)
                    && (query.PartOfSpeech == null || meaning.NormalizedPartOfSpeech == query.PartOfSpeech)))?.WordId).ToArray();
            var ids = resolved.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0)
                return queries.Select(query => new VocabularyFamilyLookup(query.WordId, false, [])).ToArray();

            var confirmed = context.VocabularyRelations.AsNoTracking().Where(row => row.Status == "confirmed");
            var edges = await confirmed.Where(row => EF.Parameter(ids).Contains(row.SourceWordId))
                .Concat(confirmed.Where(row => EF.Parameter(ids).Contains(row.TargetWordId) && !EF.Parameter(ids).Contains(row.SourceWordId)))
                .Select(row => new FamilyEdge(row.Id, row.SourceWordId, row.TargetWordId, row.Type, row.UsageNote, row.OriginKind))
                .ToArrayAsync(cancellationToken);
            var relatedIds = edges.SelectMany(edge => new[] { edge.SourceWordId, edge.TargetWordId }).Distinct(StringComparer.Ordinal).ToArray();
            var missingIds = relatedIds.Where(id => !words.ContainsKey(id)).ToArray();
            if (missingIds.Length != 0)
            {
                var relatedWords = await SelectFamilyWords(catalog.Where(word => EF.Parameter(missingIds).Contains(word.Id)))
                    .ToArrayAsync(cancellationToken);
                foreach (var word in relatedWords) words.Add(word.WordId, word);
            }

            var meaningRows = await enabledMeanings.Where(row => EF.Parameter(relatedIds).Contains(row.Meaning.VocabularyId))
                .OrderBy(row => row.Meaning.VocabularyId).ThenBy(row => row.BookName).ThenBy(row => row.Meaning.BookId)
                .ThenBy(row => row.Meaning.PartOfSpeech).ThenBy(row => row.Meaning.Meaning).ThenBy(row => row.Meaning.Id)
                .Select(row => new
                {
                    row.Meaning.VocabularyId,
                    Details = new VocabularyFamilyMeaning(row.Meaning.BookId, row.BookName, row.Meaning.Id, row.Meaning.PartOfSpeech,
                        row.Meaning.Meaning, row.MeaningKey, row.Meaning.Example)
                }).ToArrayAsync(cancellationToken);
            var meanings = meaningRows.GroupBy(row => row.VocabularyId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(row => row.Details).ToArray(), StringComparer.Ordinal);

            var outgoing = edges.ToLookup(edge => edge.SourceWordId, StringComparer.Ordinal);
            var incoming = edges.Where(edge => edge.SourceWordId != edge.TargetWordId).ToLookup(edge => edge.TargetWordId, StringComparer.Ordinal);
            var results = new Dictionary<string, VocabularyFamilyLookup>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = outgoing[id].Select(edge => Project(edge, "outgoing", edge.TargetWordId))
                    .Concat(incoming[id].Select(edge => Project(edge, "incoming", edge.SourceWordId)))
                    .OrderBy(item => FamilyTypeOrder[item.Type])
                    .ThenBy(item => item.Word.NormalizedWord, StringComparer.Ordinal)
                    .ThenBy(item => item.RelationId, StringComparer.Ordinal).ToArray();
                results.Add(id, new(id, true, items));
            }
            return resolved.Select((id, index) => id == null ? new VocabularyFamilyLookup(queries[index].WordId, false, []) : results[id]).ToArray();

            IEnumerable<VocabularyFamilyWord> CandidatesFor(VocabularyFamilyQuery query) => query.WordId is string id
                ? words.TryGetValue(id, out var word) ? [word] : []
                : wordsBySpelling[query.Word!];

            VocabularyFamilyRelation Project(FamilyEdge edge, string direction, string otherWordId) =>
                new(edge.Id, edge.Type, direction, edge.UsageNote, edge.OriginKind, words[otherWordId], meanings.GetValueOrDefault(otherWordId) ?? []);
        }, cancellationToken);

    private static IQueryable<VocabularyFamilyWord> SelectFamilyWords(IQueryable<VocabularyEntity> words) =>
        words.Select(word => new VocabularyFamilyWord(word.Id, word.Word, word.NormalizedWord, word.PhoneticUk, word.PhoneticUs));

    private sealed record FamilyEdge(string Id, string SourceWordId, string TargetWordId, string Type, string? UsageNote, string OriginKind);
}
