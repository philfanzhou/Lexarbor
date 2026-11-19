using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyFamilyService(IVocabularyWordListRepository words, IVocabNotebookRepository items,
    IVocabularyFamilyCatalog catalog, VocabularyMemoryService memory, VocabularyNotebookService notebook, TimeProvider clock)
{
    private const int MaxFamilyLookupItems = 100;
    private const int MaxConcurrentFamilyLookups = 4;

    public async Task<VocabularyFamilySources> SourcesAsync(Guid studentId, VocabNotebookActor actor,
        string? query, int page, int size, CancellationToken cancellationToken)
    {
        if (!VocabularyWordListService.ValidQuery(query) || !VocabularyErrorProneService.ValidPaging(page, size))
            throw new VocabNotebookInvalidException();
        var filter = query?.Trim();
        var source = await ReadSourcesAsync(studentId, actor, filter, exact: false, cancellationToken).ConfigureAwait(false);
        if (source.Eligible.Count == 0) return new(page, size, 0, []);

        var notebookItems = await items.ListAsync(studentId, cancellationToken).ConfigureAwait(false);
        var availability = new CandidateAvailability(source.Learned, notebookItems);
        var offset = (page - 1) * size;
        var total = 0;
        var result = new List<VocabularyFamilySourceSummary>(size);
        foreach (var wave in source.Eligible.Chunk(MaxFamilyLookupItems * MaxConcurrentFamilyLookups))
        {
            var lookups = await LookupAsync(wave.SelectMany(word => word.WordIds), cancellationToken).ConfigureAwait(false);
            foreach (var word in wave)
            {
                var addable = RelationsFor(word, lookups)
                    .Where(relation => relation.WordKey != word.Key && relation.Meanings.Any(meaning => availability.CanSelect(
                        meaning, Link(word, relation, meaning))))
                    .Select(relation => relation.WordKey).Distinct(StringComparer.Ordinal).Count();
                if (addable == 0) continue;
                if (total >= offset && result.Count < size)
                    result.Add(new(word.Key, word.Word, word.LastReviewedAt, addable));
                total++;
            }
        }
        return new(page, size, total, result);
    }

    public async Task<VocabularyFamilyCandidates> CandidatesAsync(Guid studentId, VocabNotebookActor actor,
        string wordKey, CancellationToken cancellationToken)
    {
        var source = await ReadSourcesAsync(studentId, actor, Required(wordKey), exact: true, cancellationToken).ConfigureAwait(false);
        var word = source.Eligible.SingleOrDefault() ?? throw new VocabularyFamilySourceForbiddenException();
        var relations = await GetRelationsAsync(word, cancellationToken).ConfigureAwait(false);
        var notebookItems = await items.ListAsync(studentId, cancellationToken).ConfigureAwait(false);
        var candidates = BuildCandidates(word, relations, new CandidateAvailability(source.Learned, notebookItems));
        return new(word.Key, word.Word, candidates.Where(candidate => candidate.IsDerived).ToArray(),
            candidates.Where(candidate => !candidate.IsDerived).ToArray());
    }

    public async Task<IReadOnlyList<VocabularyFamilyAddResult>> AddAsync(Guid studentId, VocabNotebookActor actor,
        string? sourceWordKey, IReadOnlyList<VocabularyFamilySelection?>? selections, CancellationToken cancellationToken)
    {
        if (selections is null || selections.Count is < 1 or > 20
            || selections.Any(selection => selection is null || !ValidId(selection.RelationId) || !ValidId(selection.MeaningId)))
            throw new VocabNotebookInvalidException();

        var source = await ReadSourcesAsync(studentId, actor, Required(sourceWordKey), exact: true, cancellationToken).ConfigureAwait(false);
        var word = source.Eligible.SingleOrDefault() ?? throw new VocabularyFamilySourceForbiddenException();
        var relations = (await GetRelationsAsync(word, cancellationToken).ConfigureAwait(false))
            .ToDictionary(relation => relation.RelationId, StringComparer.Ordinal);
        var results = new List<VocabularyFamilyAddResult>(selections.Count);
        foreach (var selection in selections.OfType<VocabularyFamilySelection>())
        {
            var status = "invalid-relation";
            if (relations.TryGetValue(selection.RelationId!, out var relation) && relation.WordKey != word.Key)
            {
                var meaning = relation.Meanings.FirstOrDefault(item => item.MeaningId == selection.MeaningId);
                status = meaning is null ? "invalid-meaning" : "in-notebook";
                if (meaning is not null)
                {
                    status = await notebook.AddFamilyAsync(studentId, actor, meaning, Link(word, relation, meaning), cancellationToken).ConfigureAwait(false);
                }
            }
            results.Add(new(selection.RelationId!, selection.MeaningId!, status));
        }
        return results;
    }

    private async Task<IReadOnlyList<VocabularyFamilyRelation>> GetRelationsAsync(Source word, CancellationToken cancellationToken)
    {
        if (word.WordIds.Count == 1)
            return await catalog.GetFamilyAsync(word.WordIds[0], cancellationToken).ConfigureAwait(false);
        var lookups = await LookupAsync(word.WordIds, cancellationToken).ConfigureAwait(false);
        return RelationsFor(word, lookups).ToArray();
    }

    private async Task<Dictionary<string, IReadOnlyList<VocabularyFamilyRelation>>> LookupAsync(
        IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<VocabularyFamilyRelation>>(StringComparer.Ordinal);
        foreach (var wave in ids.Distinct(StringComparer.Ordinal).Chunk(MaxFamilyLookupItems).Chunk(MaxConcurrentFamilyLookups))
        {
            var lookups = await Task.WhenAll(wave.Select(batch => catalog.LookupFamiliesAsync(batch, cancellationToken))).ConfigureAwait(false);
            foreach (var pair in lookups.SelectMany(lookup => lookup))
                result.Add(pair.Key, pair.Value);
        }
        return result;
    }

    private static IEnumerable<VocabularyFamilyRelation> RelationsFor(Source word, IReadOnlyDictionary<string, IReadOnlyList<VocabularyFamilyRelation>> lookups) =>
        word.WordIds.SelectMany(id => lookups[id]).DistinctBy(relation => relation.RelationId, StringComparer.Ordinal);

    private async Task<SourceSnapshot> ReadSourcesAsync(Guid studentId, VocabNotebookActor actor, string? query, bool exact,
        CancellationToken cancellationToken)
    {
        await using var snapshot = await words.OpenAsync(studentId, cancellationToken).ConfigureAwait(false);
        if (!snapshot.StudentExists) throw new VocabStudentNotFoundException();
        var cards = await snapshot.ReadCardsAsync(cancellationToken).ConfigureAwait(false);
        var learned = cards.Select(card => new CardKey(card.WordKey, card.MeaningKey)).ToHashSet();
        var eligible = cards.GroupBy(card => card.WordKey, StringComparer.Ordinal)
            .Where(group => (string.IsNullOrEmpty(query) || (exact ? group.Key == query : group.Key.Contains(query, StringComparison.OrdinalIgnoreCase)))
                && group.Any(IsEligible))
            .ToArray();
        var keys = eligible.SelectMany(group => group.Select(card => new CardKey(card.WordKey, card.MeaningKey))).ToArray();
        var displays = await snapshot.ReadSnapshotsAsync(keys, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var sources = new List<Source>(eligible.Length);
        foreach (var group in eligible)
        {
            var available = group.Where(IsEligible).OrderByDescending(card => card.LastReviewedAt).ThenBy(card => card.MeaningKey, StringComparer.Ordinal)
                .Select(card => displays.GetValueOrDefault(new(card.WordKey, card.MeaningKey)))
                .Where(display => display?.BookId != VocabNotebook.SubmissionBookId && !string.IsNullOrWhiteSpace(display?.BookId)
                    && !string.IsNullOrWhiteSpace(display.WordId)
                    && !string.IsNullOrWhiteSpace(display.MeaningId))
                .ToArray();
            if (available.FirstOrDefault() is not { } latest) continue;
            var (displayWord, _, _) = VocabularyCardDisplay.Read(group.Key, group.First().MeaningKey, latest);
            var references = available.Select(display => new VocabularyCatalogReference(display!.WordId!, display.BookId!, display.MeaningId!))
                .ToHashSet();
            var wordIds = available.Select(display => display!.WordId!).Distinct(StringComparer.Ordinal).ToArray();
            sources.Add(new(group.Key, displayWord, wordIds, group.Max(card => card.LastReviewedAt), references));
        }
        return new(sources.OrderByDescending(word => word.LastReviewedAt).ThenBy(word => word.Key, StringComparer.Ordinal).ToArray(), learned);

        bool IsEligible(VocabularyWordListCard card) => actor.Type == VocabNotebookActorType.Teacher
            || memory.GetStage(new MemoryCardModel
            {
                StudentId = studentId,
                WordKey = card.WordKey,
                MeaningKey = card.MeaningKey,
                Stability = card.Stability
            }) >= MemoryStage.Familiar;
    }

    private static IReadOnlyList<VocabularyFamilyCandidate> BuildCandidates(Source source, IReadOnlyList<VocabularyFamilyRelation> relations,
        CandidateAvailability availability)
    {
        return relations.Where(relation => relation.WordKey != source.Key).Select(relation =>
        {
            var matched = new List<string>();
            var meanings = relation.Meanings.Select(meaning =>
            {
                var existing = availability.FindItem(meaning);
                var verified = MatchSource(source, relation, meaning) is not null;
                var selectable = availability.CanSelect(meaning, Link(source, relation, meaning));
                if (verified && selectable) matched.Add(meaning.MeaningId);
                return new VocabularyFamilyMeaning(meaning.MeaningId, meaning.PartOfSpeech, meaning.Meaning, meaning.BookName,
                    existing is not null, selectable);
            }).ToArray();
            var preferred = matched.Count == 1 ? matched[0] : null;
            var status = relation.Meanings.Count > 0 && relation.Meanings.All(availability.IsLearned) ? "learned"
                : meanings.Any(meaning => meaning.InNotebook) ? "in-notebook"
                : meanings.Length == 0 ? "unavailable" : "addable";
            return new VocabularyFamilyCandidate(relation.RelationId, relation.Type, relation.Direction, relation.UsageNote,
                relation.Word, relation.WordKey, status, preferred, matched.Count > 0, meanings);
        }).ToArray();
    }

    private static VocabularyCatalogReference? MatchSource(Source source, VocabularyFamilyRelation relation, VocabCatalogMeaning meaning)
    {
        var target = new VocabularyCatalogReference(meaning.WordId, meaning.BookId, meaning.MeaningId);
        return relation.MeaningPairs.Where(pair => relation.To(pair) == target)
            .Select(relation.From).FirstOrDefault(source.References.Contains);
    }

    private static string Required(string? wordKey) =>
        !string.IsNullOrWhiteSpace(wordKey) && wordKey.Length <= VocabNotebook.MaxWordLength && !wordKey.Contains('\0')
            ? wordKey : throw new VocabNotebookInvalidException();

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && !id.Contains('\0');

    private static VocabNotebookFamilySource Link(Source source, VocabularyFamilyRelation relation, VocabCatalogMeaning meaning)
    {
        var match = MatchSource(source, relation, meaning);
        return new(source.Key, source.Word, match?.WordId ?? relation.QueryWordId ?? source.WordIds[0],
            relation.Type, relation.Direction, relation.RelationId, match?.MeaningId, match?.BookId,
            new(meaning.WordId, meaning.BookId, meaning.MeaningId));
    }

    private sealed class CandidateAvailability(IReadOnlySet<CardKey> learned, IReadOnlyList<VocabNotebookItemModel> notebookItems)
    {
        private readonly Dictionary<CardKey, VocabNotebookItemModel> _byKey = notebookItems.ToDictionary(item => item.Key);
        public VocabNotebookItemModel? FindItem(VocabCatalogMeaning meaning) => _byKey.GetValueOrDefault(new(meaning.WordKey, meaning.MeaningKey));
        public bool IsLearned(VocabCatalogMeaning meaning) => learned.Contains(new(meaning.WordKey, meaning.MeaningKey));
        public bool CanSelect(VocabCatalogMeaning meaning, VocabNotebookFamilySource source) =>
            FindItem(meaning)?.CanAttachFamily(source) ?? true;
    }

    private sealed record Source(string Key, string Word, IReadOnlyList<string> WordIds, DateTimeOffset LastReviewedAt,
        IReadOnlySet<VocabularyCatalogReference> References);
    private sealed record SourceSnapshot(IReadOnlyList<Source> Eligible, IReadOnlySet<CardKey> Learned);
}

public sealed class VocabularyFamilySourceForbiddenException : Exception
{
    public VocabularyFamilySourceForbiddenException() : base("This word is not eligible for family expansion.") { }
}
