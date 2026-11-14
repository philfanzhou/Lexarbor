using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed partial class VocabularyPublicQueryRepository
{
    /// <summary>Reads the directed link, competing answers and form choices in a single snapshot.</summary>
    public Task<LearningQuestionRelationContext?> GetLearningRelationAsync(string wordId, LearningQuestionRelation relation,
        CancellationToken cancellationToken) => SnapshotAsync<LearningQuestionRelationContext?>(async () =>
    {
        var confirmed = context.VocabularyRelations.AsNoTracking().Where(row => row.Status == "confirmed");
        var words = context.Vocabularies.AsNoTracking();
        var match = await (from edge in confirmed
                           join anchor in words on edge.SourceWordId equals anchor.Id
                           join target in words on edge.TargetWordId equals target.Id
                           where anchor.Id == relation.SourceWordId && target.Id == wordId && edge.Type == relation.Type
                           select new { Anchor = anchor.Word, AnchorKey = anchor.NormalizedWord, TargetKey = target.NormalizedWord })
            .SingleOrDefaultAsync(cancellationToken);
        if (match == null || string.IsNullOrWhiteSpace(match.Anchor)) return null;

        // The stem displays spellings. An equivalent spelling in a legacy
        // duplicate row can make a second relation code a valid answer too.
        var displayedTypes = await (from edge in confirmed
                                    join anchor in words on edge.SourceWordId equals anchor.Id
                                    join target in words on edge.TargetWordId equals target.Id
                                    where anchor.NormalizedWord == match.AnchorKey && target.NormalizedWord == match.TargetKey
                                    select edge.Type).Distinct().ToArrayAsync(cancellationToken);
        var anchorId = relation.SourceWordId;
        var confirmedFromAnchor = from edge in confirmed
                                  join source in words on edge.SourceWordId equals source.Id
                                  where edge.Type == relation.Type && source.NormalizedWord == match.AnchorKey
                                  join target in words on edge.TargetWordId equals target.Id
                                  select target.NormalizedWord;
        var candidates = confirmed.Where(edge => edge.SourceWordId == anchorId).Select(edge => edge.TargetWordId)
            .Union(SharedSourceTargets(confirmed, anchorId));
        var distractors = await words.Where(word => (word.Id == anchorId || candidates.Contains(word.Id))
                && word.NormalizedWord != match.TargetKey && !confirmedFromAnchor.Contains(word.NormalizedWord)
                && word.NormalizedWord != string.Empty)
            .GroupBy(word => word.NormalizedWord).Select(group => group.Min(word => word.Word)!)
            .OrderBy(_ => EF.Functions.Random()).ToArrayAsync(cancellationToken);
        return new(match.Anchor, displayedTypes, distractors.Where(text => !string.IsNullOrWhiteSpace(text))
            .Where(text => !string.Equals(text.Trim(), match.TargetKey, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(text => text.Trim(), StringComparer.OrdinalIgnoreCase).Take(3).ToArray());
    }, cancellationToken);

    /// <summary>Returns null when the target has no family so review can use its ordinary choice generator.</summary>
    public Task<IReadOnlyList<VocabularyQuestionOptionModel>?> GetReviewDistractorsAsync(VocabularyModel word,
        VocabularyMeaningModel meaning, VocabularyDistractorScope? scope, bool chineseToEnglish, bool supplementary,
        CancellationToken cancellationToken) => SnapshotAsync<IReadOnlyList<VocabularyQuestionOptionModel>?>(async () =>
    {
        var confirmed = context.VocabularyRelations.AsNoTracking().Where(row => row.Status == "confirmed");
        var wordId = word.Id;
        var family = confirmed.Where(row => row.SourceWordId == wordId).Select(row => row.TargetWordId)
            .Union(confirmed.Where(row => row.TargetWordId == wordId).Select(row => row.SourceWordId))
            .Union(SharedSourceTargets(confirmed, wordId)).Where(id => id != wordId);
        if (!await family.AnyAsync(cancellationToken)) return null;

        var wordKey = CatalogKeys.Of(word.Word);
        var meaningKey = CatalogKeys.Of(meaning.Meaning);
        var words = context.Vocabularies.AsNoTracking();
        var enabled = from item in context.VocabularyMeanings.AsNoTracking()
                      join book in context.VocabularyBooks.AsNoTracking() on item.BookId equals book.Id
                      where book.Status
                      select item;
        var stemAnswers = from item in enabled
                          join entry in words on item.VocabularyId equals entry.Id
                          where item.NormalizedMeaning.ToLower() == meaningKey
                          select entry.NormalizedWord;
        var targetMeanings = from item in enabled
                             join entry in words on item.VocabularyId equals entry.Id
                             where entry.NormalizedWord == wordKey
                             select item.NormalizedMeaning.ToLower();
        var otherWords = words.Where(entry => entry.NormalizedWord != wordKey);
        var eligible = chineseToEnglish
            ? otherWords.Where(entry => !stemAnswers.Contains(entry.NormalizedWord))
            : otherWords;
        var answers = new ReviewOptions();
        var answerText = (chineseToEnglish ? word.Word : meaning.Meaning).Trim();
        await AddOptionsAsync(enabled.Where(item => family.Contains(item.VocabularyId)));
        if (answers.Count < 3)
            await AddOptionsAsync(ScopedMeanings(enabled, meaning.BookId, scope, chineseToEnglish));
        if (answers.Count < 3 && supplementary)
            await AddOptionsAsync(enabled);
        return answers.ToOptions();

        async Task AddOptionsAsync(IQueryable<VocabularyMeaningEntity> meanings)
        {
            var candidates = chineseToEnglish
                ? (from item in meanings
                   join entry in eligible on item.VocabularyId equals entry.Id
                   select new ReviewOption(entry.Id, entry.NormalizedWord, entry.Word, null)).Distinct()
                : from item in meanings
                  join entry in eligible on item.VocabularyId equals entry.Id
                  let textKey = item.NormalizedMeaning.ToLower()
                  where textKey != meaningKey && !targetMeanings.Contains(textKey)
                  select new ReviewOption(entry.Id, entry.NormalizedWord, item.Meaning, item.Id);
            // Randomize eligible rows before combining family and fallback options.
            await foreach (var candidate in candidates.OrderBy(_ => EF.Functions.Random()).AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(candidate.Text)) continue;
                if (string.Equals(candidate.TextKey, answerText, StringComparison.OrdinalIgnoreCase)) continue;
                answers.Add(candidate);
                if (answers.Count == 3) return;
            }
        }
    }, cancellationToken);

    private IQueryable<VocabularyMeaningEntity> ScopedMeanings(IQueryable<VocabularyMeaningEntity> enabled,
        string bookId, VocabularyDistractorScope? scope, bool chineseToEnglish)
    {
        var meanings = enabled.Where(item => item.BookId == bookId);
        var unitId = scope?.UnitId;
        var positions = context.VocabularyMeaningUnits.AsNoTracking();
        if (unitId != null)
        {
            positions = positions.Where(position => position.UnitId == unitId);
            meanings = meanings.Where(item => positions.Any(position => position.MeaningId == item.Id));
        }
        if (scope?.PhraseOnly is not bool phraseOnly) return meanings;

        var phrasePositions = positions.Where(position => position.EntryKind == "phrase");
        if (!chineseToEnglish)
            return meanings.Where(item => phrasePositions.Any(position => position.MeaningId == item.Id) == phraseOnly);
        return meanings.Where(item => meanings.Any(other => other.VocabularyId == item.VocabularyId
            && phrasePositions.Any(position => position.MeaningId == other.Id)) == phraseOnly);
    }

    private static IQueryable<string> SharedSourceTargets(IQueryable<VocabularyRelationEntity> confirmed, string wordId) =>
        from incoming in confirmed
        where incoming.TargetWordId == wordId
        join sibling in confirmed on incoming.SourceWordId equals sibling.SourceWordId
        select sibling.TargetWordId;

    private sealed record ReviewOption(string WordId, string WordKey, string Text, string? MeaningId)
    {
        public string TextKey => Text.Trim();
    }

    private sealed class ReviewOptions
    {
        private readonly Dictionary<string, List<ReviewOption>> _candidates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _wordsPerText = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ReviewOption> _byText = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _assigned = new(StringComparer.OrdinalIgnoreCase);
        public int Count => _byText.Count;

        public void Add(ReviewOption option)
        {
            var wordCount = _wordsPerText.GetValueOrDefault(option.TextKey);
            if (wordCount == 3) return;
            if (!_candidates.TryGetValue(option.WordKey, out var candidates))
                _candidates.Add(option.WordKey, candidates = []);
            // Each choice can collide with at most two others. Retaining up to
            // three alternatives for either side suffices to fill three slots.
            if (candidates.Count == 3 || candidates.Any(candidate => string.Equals(candidate.TextKey, option.TextKey, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add(option);
            _wordsPerText[option.TextKey] = wordCount + 1;

            // A newly seen meaning can free a text another word needs. Keep one
            // meaning per spelling and try alternate meanings before dropping an option.
            foreach (var word in _candidates.Keys)
            {
                if (_assigned.Contains(word) || !TryAssign(word, new HashSet<string>(StringComparer.OrdinalIgnoreCase))) continue;
                _assigned.Add(word);
                break;
            }
        }

        private bool TryAssign(string word, HashSet<string> visitedTexts)
        {
            foreach (var candidate in _candidates[word])
            {
                if (!visitedTexts.Add(candidate.TextKey)) continue;
                if (_byText.TryGetValue(candidate.TextKey, out var previous) && !TryAssign(previous.WordKey, visitedTexts)) continue;
                _byText[candidate.TextKey] = candidate;
                return true;
            }
            return false;
        }

        public VocabularyQuestionOptionModel[] ToOptions() => _byText.Values.Select(option => new VocabularyQuestionOptionModel
        {
            Text = option.Text,
            WordId = option.WordId,
            MeaningId = option.MeaningId
        }).ToArray();
    }
}
