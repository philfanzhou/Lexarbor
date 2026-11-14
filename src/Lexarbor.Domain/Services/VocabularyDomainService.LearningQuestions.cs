using System.Text;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public partial class VocabularyDomainService
{
    private readonly LearningQuestionGenerator _learningQuestions;
    private static readonly string[] DerivedTypes = ["derived_noun", "derived_verb", "derived_adjective", "derived_adverb"];
    private static readonly string[] VerbFormTypes = ["past_tense", "past_participle", "ing_form", "third_person_singular"];

    public Task<LearningQuestion> CreateLearningQuestionAsync(
        string wordId, string bookId, string meaningId, LearningQuestionMode mode,
        string? unitId = null, bool sameEntryKind = false, CancellationToken cancellationToken = default,
        LearningQuestionRelation? relation = null) =>
        CreateLearningQuestionCoreAsync(wordId, bookId, meaningId, mode, unitId, sameEntryKind, relation, useFamily: true, cancellationToken);

    internal Task<LearningQuestion> CreateSubmissionLearningQuestionAsync(
        string wordId, string bookId, string meaningId, LearningQuestionMode mode, CancellationToken cancellationToken) =>
        CreateLearningQuestionCoreAsync(wordId, bookId, meaningId, mode, null, false, null, useFamily: false, cancellationToken);

    private async Task<LearningQuestion> CreateLearningQuestionCoreAsync(
        string wordId, string bookId, string meaningId, LearningQuestionMode mode, string? unitId, bool sameEntryKind,
        LearningQuestionRelation? relation, bool useFamily, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(wordId) || string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(meaningId))
            throw new DomainValidationException("Word ID, book ID and meaning ID are required.");
        if (relation != null && (string.IsNullOrWhiteSpace(relation.SourceWordId) || !VocabularyRelationTypes.IsValid(relation.Type)))
            throw new DomainValidationException("A source word ID and a valid relation type are required.");

        var (word, meaning, scope) = await ResolveQuestionTargetAsync(
            wordId, bookId, meaningId, unitId, sameEntryKind, cancellationToken);
        var reviewCatalog = useFamily && mode == LearningQuestionMode.Review && _catalogQueries != null;
        var related = reviewCatalog && relation != null
            ? await _catalogQueries!.GetLearningRelationAsync(wordId, relation, cancellationToken)
            : null;
        return await _learningQuestions.CreateAsync(word.Word, meaning.Meaning, mode,
            (direction, token) => reviewCatalog
                ? BuildReviewQuestionAsync(word, meaning, scope, direction, token)
                : BuildQuestionAsync(word, meaning, scope, direction, token),
            cancellationToken, related == null ? null : type => BuildRelatedQuestion(type, word, meaning, relation!, related));
    }

    private async Task<VocabularyQuestionModel?> BuildReviewQuestionAsync(VocabularyModel word, VocabularyMeaningModel meaning,
        VocabularyDistractorScope? scope, bool chineseToEnglish, CancellationToken token)
    {
        var supplementary = await IsSupplementaryBookAsync(meaning.BookId, token);
        var distractors = await _catalogQueries!.GetReviewDistractorsAsync(word, meaning, scope, chineseToEnglish, supplementary, token);
        if (distractors == null) return await BuildQuestionAsync(word, meaning, scope, chineseToEnglish, token);
        if (distractors.Count != 3) return null;

        var correct = chineseToEnglish ? word.Word : meaning.Meaning;
        List<VocabularyQuestionOptionModel> options = [new() { Text = correct, IsCorrect = true, WordId = word.Id, MeaningId = meaning.Id }, .. distractors];
        Shuffle(options);
        return new()
        {
            Word = chineseToEnglish ? meaning.Meaning : word.Word,
            WordId = word.Id,
            MeaningId = meaning.Id,
            ChineseToEnglish = chineseToEnglish,
            Options = options
        };
    }

    private static LearningQuestion? BuildRelatedQuestion(LearningQuestionType type, VocabularyModel word, VocabularyMeaningModel meaning,
        LearningQuestionRelation relation, LearningQuestionRelationContext related)
    {
        switch (type)
        {
            case LearningQuestionType.FormChoice:
                return related.FormDistractors.Count == 3
                    ? new FormChoiceLearningQuestion(related.AnchorWord, relation.Type, BuildOptions(word.Word, related.FormDistractors))
                    : null;
            case LearningQuestionType.RelationChoice:
                var group = DerivedTypes.Contains(relation.Type) ? DerivedTypes : VerbFormTypes;
                return group.Contains(relation.Type) && related.Types.Count(group.Contains) == 1
                    ? new RelationChoiceLearningQuestion(word.Word, related.AnchorWord, BuildOptions(relation.Type, group.Where(code => code != relation.Type)))
                    : null;
            case LearningQuestionType.FormFill:
                var pattern = CreateFormPattern(related.AnchorWord, word.Word);
                return pattern == null ? null : new FormFillLearningQuestion(meaning.Meaning, related.AnchorWord, relation.Type, pattern, word.Word);
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static List<VocabularyQuestionOptionModel> BuildOptions(string answer, IEnumerable<string> distractors)
    {
        List<VocabularyQuestionOptionModel> options = [new() { Text = answer, IsCorrect = true }];
        options.AddRange(distractors.Select(text => new VocabularyQuestionOptionModel { Text = text }));
        Shuffle(options);
        return options;
    }

    private static string?[]? CreateFormPattern(string anchor, string answer)
    {
        if (CatalogKeys.Of(anchor) == CatalogKeys.Of(answer)) return null;

        var source = anchor.EnumerateRunes().ToArray();
        var target = answer.EnumerateRunes().ToArray();
        var limit = Math.Min(source.Length, target.Length);
        var prefix = 0;
        while (prefix < limit && Rune.ToUpperInvariant(source[prefix]) == Rune.ToUpperInvariant(target[prefix])) prefix++;
        var suffix = 0;
        while (suffix < limit && Rune.ToUpperInvariant(source[^(suffix + 1)]) == Rune.ToUpperInvariant(target[^(suffix + 1)])) suffix++;

        // An unrelated spelling keeps its first letter as the clue.
        if (prefix == 0 && suffix == 0)
            prefix = Array.FindIndex(target, Rune.IsLetter) + 1;
        var pattern = new string?[target.Length];
        for (var index = 0; index < target.Length; index++)
        {
            var unchanged = prefix >= suffix ? index < prefix : index >= target.Length - suffix;
            if (unchanged || !Rune.IsLetter(target[index])) pattern[index] = target[index].ToString();
        }
        return pattern.Contains(null) ? pattern : null;
    }
}
