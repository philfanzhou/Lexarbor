using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed partial class VocabularyPublicQueryRepository
{
    public Task<PartOfSpeechChoiceCandidates?> GetPartOfSpeechChoiceAsync(VocabularyModel word, VocabularyMeaningModel meaning,
        VocabularyPartOfSpeech partOfSpeech, VocabularyDistractorScope? scope, bool supplementary, CancellationToken cancellationToken) =>
        SnapshotAsync<PartOfSpeechChoiceCandidates?>(async () =>
        {
            var confirmed = context.VocabularyRelations.AsNoTracking().Where(edge => edge.Status == "confirmed");
            var derivatives = confirmed.Where(edge => edge.Type == "derived_noun" || edge.Type == "derived_verb"
                || edge.Type == "derived_adjective" || edge.Type == "derived_adverb");
            var clues = FamilyIds(derivatives, word.Id);
            var family = FamilyIds(confirmed, word.Id);
            var words = context.Vocabularies.AsNoTracking();
            var enabled = EnabledQuestionMeanings();
            var targetKey = CatalogKeys.Of(word.Word);
            var meaningKey = CatalogKeys.Of(meaning.Meaning);
            var targetAliases = partOfSpeech.Aliases.ToArray();
            var knownAliases = VocabularyPartOfSpeech.KnownAliases.ToArray();

            var otherWord = words.Where(candidate => candidate.NormalizedWord != targetKey && candidate.NormalizedWord != string.Empty);
            var clueTexts = from entry in otherWord
                            join item in enabled on entry.Id equals item.VocabularyId
                            where clues.Contains(entry.Id) && EF.Parameter(knownAliases).Contains(item.NormalizedPartOfSpeech)
                                && !EF.Parameter(targetAliases).Contains(item.NormalizedPartOfSpeech)
                            select entry.Word;
            var anchors = (await clueTexts.Distinct().ToArrayAsync(cancellationToken))
                .Where(text => !string.IsNullOrWhiteSpace(text) && !SameSpelling(text, word.Word))
                .DistinctBy(text => text.Trim(), StringComparer.OrdinalIgnoreCase).ToArray();
            if (anchors.Length == 0) return null;

            // An unfamiliar or missing label on the same definition does not
            // prove that this spelling is safe to display as a wrong answer.
            var ambiguousSpellings = from entry in words
                                     join item in enabled on entry.Id equals item.VocabularyId
                                     where item.NormalizedMeaning.ToLower() == meaningKey
                                         && (EF.Parameter(targetAliases).Contains(item.NormalizedPartOfSpeech)
                                             || !EF.Parameter(knownAliases).Contains(item.NormalizedPartOfSpeech))
                                     select entry.NormalizedWord;
            if (await otherWord.Where(entry => family.Contains(entry.Id))
                    .AnyAsync(entry => ambiguousSpellings.Any(spelling => spelling == entry.NormalizedWord), cancellationToken))
                return null;

            var eligible = otherWord.Where(entry => !ambiguousSpellings.Any(spelling => spelling == entry.NormalizedWord));
            // Keep one extra candidate so the selected clue can be removed
            // without reducing the number of wrong options.
            var wrong = new List<string>(4);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { word.Word.Trim() };
            await AddOptionsAsync(enabled.Where(item => family.Contains(item.VocabularyId)));
            if (NeedsMoreOptions())
                await AddOptionsAsync(ScopedMeanings(enabled, meaning.BookId, scope, chineseToEnglish: true));
            if (NeedsMoreOptions() && supplementary)
                await AddOptionsAsync(enabled);

            var usableAnchors = wrong.Count == 4 ? anchors : anchors.Where(anchor => !seen.Contains(anchor.Trim())).ToArray();
            if (wrong.Count < 3 || usableAnchors.Length == 0) return null;
            var anchor = usableAnchors[Random.Shared.Next(usableAnchors.Length)];
            return new(anchor, wrong.Where(text => !SameSpelling(text, anchor)).Take(3).ToArray());

            bool NeedsMoreOptions() => wrong.Count < 3 || (wrong.Count == 3 && anchors.All(anchor => seen.Contains(anchor.Trim())));

            async Task AddOptionsAsync(IQueryable<VocabularyMeaningEntity> meanings)
            {
                var choices = from item in meanings
                              join entry in eligible on item.VocabularyId equals entry.Id
                              select entry.Word;
                await foreach (var text in choices.Distinct().OrderBy(_ => EF.Functions.Random())
                    .AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    if (!string.IsNullOrWhiteSpace(text) && seen.Add(text.Trim())) wrong.Add(text);
                    if (!NeedsMoreOptions()) return;
                }
            }
        }, cancellationToken);

    private static bool SameSpelling(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
