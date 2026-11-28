using System.Collections.Concurrent;
using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;

namespace Lexarbor.Learning.Service;

/// <summary>
/// Generates unit practice questions through the catalog's on-demand learning-question route.
/// Choice sessions only draw Chinese-to-English choices; mixed sessions use the review draw. Both
/// prefer distractors from the practiced unit, then from the whole book. A target for which neither
/// produces a question is asked as a full spelling.
/// </summary>
public sealed class UnitPracticeQuestionSource(ICatalogClient catalog)
{
    private const int Parallelism = 4;
    private static readonly string[] ChoiceTypes = [nameof(VocabQuestionDirection.ChineseToEnglish)];

    /// <summary>One question per target, null meaning a spelling question. Catalog outages propagate.</summary>
    public async Task<UnitPracticeIssued?[]> CreateAsync(UnitPracticeMode mode, string bookId, string unitId,
        IReadOnlyList<UnitPracticeTarget> targets, CancellationToken ct)
    {
        var questions = new UnitPracticeIssued?[targets.Count];
        if (mode == UnitPracticeMode.Spelling) return questions;
        await Parallel.ForEachAsync(Enumerable.Range(0, targets.Count), Options(ct), async (index, token) =>
            questions[index] = await GenerateAsync(mode, bookId, unitId, targets[index], token));
        return questions;
    }

    /// <summary>
    /// Fresh retry questions by position. A position the catalog cannot serve is left out, so the caller
    /// reissues its first-pass question. After the first outage no further calls are made.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, UnitPracticeIssued?>> RetryAsync(UnitPracticeMode mode, string bookId, string unitId,
        IReadOnlyList<UnitPracticeRetryItem> items, CancellationToken ct)
    {
        var questions = new ConcurrentDictionary<int, UnitPracticeIssued?>();
        if (mode == UnitPracticeMode.Spelling) return questions;
        var offline = 0;
        await Parallel.ForEachAsync(items, Options(ct), async (item, token) =>
        {
            if (Volatile.Read(ref offline) != 0) return;
            try
            {
                if (await GenerateAsync(mode, bookId, unitId, item.Target, token) is { } question)
                    questions[item.Position] = question;
            }
            catch (VocabCatalogUnavailableException)
            {
                Interlocked.Exchange(ref offline, 1);
            }
        });
        return questions;
    }

    private static ParallelOptions Options(CancellationToken ct) => new() { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct };

    private async Task<UnitPracticeIssued?> GenerateAsync(UnitPracticeMode mode, string bookId, string unitId,
        UnitPracticeTarget target, CancellationToken ct)
    {
        var types = mode == UnitPracticeMode.Choice ? ChoiceTypes : null;
        foreach (var unit in new string?[] { unitId, null })
        {
            var question = await catalog.GetLearningQuestionAsync(new CatalogLearningQuestionRequest(target.WordId, bookId,
                target.MeaningId, CatalogLearningQuestionReader.ReviewMode, unit, SameEntryKind: true, Types: types), ct);
            if (question != null) return Issued(question);
        }
        return null;
    }

    private static UnitPracticeIssued Issued(VocabGeneratedQuestion question) => question.Direction switch
    {
        VocabQuestionDirection.EnglishToChinese or VocabQuestionDirection.ChineseToEnglish or VocabQuestionDirection.PartOfSpeechChoice =>
            new(question.Direction.ToString(), question.Prompt,
                question.Options.Select(option => new UnitPracticeChoice(Guid.NewGuid(), option.Text, option.IsCorrect)).ToList(),
                AnchorWord: question.AnchorWord, PartOfSpeech: question.PartOfSpeech),
        VocabQuestionDirection.FillLetters when question.Pattern != null && question.AnswerWord != null =>
            new(question.Direction.ToString(), question.Prompt, Pattern: question.Pattern, AnswerWord: question.AnswerWord),
        // Related types need a relation, which unit practice never sends.
        _ => throw new VocabCatalogUnavailableException()
    };
}
