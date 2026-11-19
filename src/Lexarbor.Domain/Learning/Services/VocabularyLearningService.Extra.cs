using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

public sealed partial class VocabularyLearningService
{
    private async Task<VocabNextQuestionOutcome> GetNextExtraAsync(VocabPlanModel plan, DateOnly date, Guid? requestedId, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            var today = await _planDayRepository.FindAsync(plan.Id, date, cancellationToken).ConfigureAwait(false);
            if (today?.CompletedAt is null) throw new VocabDayNotCompletedException();
            var requestedDay = requestedId is { } lookup
                ? await _planDayRepository.FindExtraBatchDayAsync(plan.Id, lookup, cancellationToken).ConfigureAwait(false) : null;
            var day = requestedDay ?? await _planDayRepository.FindExtraBatchDayAsync(plan.Id, cancellationToken: cancellationToken).ConfigureAwait(false) ?? today;
            day.Tasks ??= new VocabDailyTasks { LegacyDay = true };
            var batches = day.Tasks.ExtraBatches;
            var batch = requestedId is { } id ? batches.FirstOrDefault(item => item.Id == id || item.RequestIds.Contains(id)) : null;
            batch ??= batches.LastOrDefault(item => item.CompletedAt is null);
            if (batch is null && requestedId is null) batch = batches.LastOrDefault();
            if (batch?.CompletedAt is not null)
            {
                if (day.NewWordsExhausted && batch.PlanVersion == plan.Version)
                {
                    var scope = await ReadScopeAsync(plan, cancellationToken).ConfigureAwait(false);
                    await _planService.RefreshTotalWordsAsync(plan.Id, plan.Version,
                        scope.Select(item => item.Entry.NormalizedWord).Distinct(StringComparer.Ordinal).Count(), cancellationToken).ConfigureAwait(false);
                }
                return VocabNextQuestionOutcome.Done("extra-batch-completed", batch.Snapshot());
            }

            var reviewKeys = today.DueInScope.Concat(today.Tasks?.FillReviews ?? []).ToList();
            if (batch is null)
            {
                if (day.NewWordsExhausted && !await HasUnlearnedNotebookMeaningsAsync(plan.StudentId, cancellationToken).ConfigureAwait(false))
                    return VocabNextQuestionOutcome.Done("scope-exhausted");
                var scope = await ReadScopeAsync(plan, cancellationToken).ConfigureAwait(false);
                var sources = await WithNotebookAsync(plan, scope, cancellationToken).ConfigureAwait(false);
                var keys = sources.Select(item => new CardKey(item.Entry.NormalizedWord, item.Entry.MeaningKey)).ToHashSet();
                // Cards belong to the student across every historical date, plan and book.
                var existing = await _cardRepository.FindExistingKeysAsync(plan.StudentId, keys, cancellationToken).ConfigureAwait(false);
                var words = sources.Where(item => !existing.Contains(new(item.Entry.NormalizedWord, item.Entry.MeaningKey)))
                    .GroupBy(item => item.Entry.NormalizedWord, StringComparer.Ordinal).ToList();
                if (words.Count == 0)
                {
                    await MarkNewWordsExhaustedAsync(plan, date, cancellationToken).ConfigureAwait(false);
                    await _planService.RefreshTotalWordsAsync(plan.Id, plan.Version,
                        scope.Select(item => item.Entry.NormalizedWord).Distinct(StringComparer.Ordinal).Count(), cancellationToken).ConfigureAwait(false);
                    return VocabNextQuestionOutcome.Done("scope-exhausted");
                }
                var held = await _questionRepository.FindPendingNewWordKeysAsync(plan.StudentId,
                    words.Select(word => word.Key).ToList(), DateTimeOffset.UtcNow, plan.Id, cancellationToken).ConfigureAwait(false);
                // Words in today's review groups wait for another day, so a word is never reviewed and learned as new on one day.
                var reviewedToday = reviewKeys.Select(key => key.WordKey).ToHashSet(StringComparer.Ordinal);
                var selected = SelectNewWords(words.Where(word => !held.Contains(word.Key) && !reviewedToday.Contains(word.Key)).ToList(),
                    plan.DailyNewWords, plan.DailyNewWords);
                batch = new VocabExtraBatch
                {
                    Id = requestedId ?? Guid.NewGuid(), Target = plan.DailyNewWords, Required = selected.Count,
                    PlanVersion = plan.Version, AllRemainingSelected = selected.Count == words.Count,
                    Words = selected.SelectMany(SnapshotNewWord).ToList(),
                };
                batches.Add(batch);
            }
            if (requestedId is { } request && request != batch.Id && !batch.RequestIds.Contains(request)) batch.RequestIds.Add(request);
            var now = DateTimeOffset.UtcNow;
            var pendingKeys = batch.Words.Select(word => new CardKey(word.WordKey, word.MeaningKey)).ToList();
            var pending = await _questionRepository.FindPendingDailyAsync(plan.Id, VocabQuestionKind.Extra,
                pendingKeys, now, cancellationToken).ConfigureAwait(false)
                ?? await _questionRepository.FindPendingDailyAsync(plan.Id, VocabQuestionKind.New,
                    pendingKeys, now, cancellationToken).ConfigureAwait(false);
            var questions = new List<VocabIssuedQuestionModel>();
            if (pending is null)
            {
                var unavailable = day.Tasks.UnavailableNewMeanings;
                var unavailableBefore = unavailable.Count;
                questions = await PickNewWordQuestionsAsync(plan, VocabQuestionKind.Extra, batch.Words, unavailable,
                    batch.LearnedWords, reviewKeys, batch.Required - batch.LearnedWords.Count, batch.Target,
                    now, cancellationToken).ConfigureAwait(false);
                if (unavailable.Count > unavailableBefore || VocabDailyTasks.WordCount(batch.Words.Select(word => new CardKey(word.WordKey, word.MeaningKey))) < batch.Required)
                    batch.AllRemainingSelected = false;
                if (questions.Count == 0)
                {
                    // Deleted or unavailable remaining targets cannot hold a batch open forever.
                    batch.Required = batch.LearnedWords.Count;
                    batch.CompletedAt ??= now;
                }
                batch.QuestionIds.AddRange(questions.Select(question => question.Id));
            }
            else if (!batch.QuestionIds.Contains(pending.Id)) batch.QuestionIds.Add(pending.Id);
            try
            {
                await _questionRepository.AddRangeAsync(questions, cancellationToken).ConfigureAwait(false);
                await SaveDailyAsync(day, true, cancellationToken).ConfigureAwait(false);
                var next = pending ?? questions.FirstOrDefault();
                return next is null ? VocabNextQuestionOutcome.Done("extra-batch-completed", batch.Snapshot())
                    : VocabNextQuestionOutcome.Next(next, batch.Snapshot());
            }
            catch (VocabPlanDayConflictException) when (retry < 4) { _unitOfWork.DiscardChanges(); }
        }
    }

    private async Task ApplyExtraProgressAsync(VocabPlanDayModel today, VocabIssuedQuestionModel question, CancellationToken cancellationToken)
    {
        var owner = today.Tasks?.ExtraBatches.Any(batch => batch.QuestionIds.Contains(question.Id)
            || batch.CompletedAt is null && batch.Words.Any(word => word.WordKey == question.WordKey)) == true ? today
            : await _planDayRepository.FindExtraBatchDayAsync(today.PlanId, question.Id, cancellationToken).ConfigureAwait(false)
                ?? await _planDayRepository.FindExtraBatchDayAsync(today.PlanId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (owner is null) return;
        if (owner.LocalDate == today.LocalDate) owner = today;
        var batch = owner.Tasks!.ExtraBatches.FirstOrDefault(item => item.QuestionIds.Contains(question.Id)
            || item.CompletedAt is null && item.Words.Any(word => word.WordKey == question.WordKey));
        if (batch is null || batch.LearnedWords.Contains(question.WordKey, StringComparer.Ordinal)) return;
        var keys = batch.Words.Where(word => word.WordKey == question.WordKey).Select(word => new CardKey(word.WordKey, word.MeaningKey)).ToHashSet();
        var existing = await _cardRepository.FindExistingKeysAsync(question.StudentId, keys, cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0 || !keys.All(existing.Contains)) return;
        batch.LearnedWords.Add(question.WordKey);
        if (batch.LearnedWords.Count >= batch.Required)
        {
            batch.CompletedAt ??= DateTimeOffset.UtcNow;
            var plan = await _planRepository.FindByIdAsync(today.PlanId, cancellationToken).ConfigureAwait(false);
            if (batch.AllRemainingSelected && plan?.Version == batch.PlanVersion) today.NewWordsExhausted = true;
        }
        if (!ReferenceEquals(owner, today))
            await _planDayRepository.UpdateAsync(owner, cancellationToken).ConfigureAwait(false);
    }

}
