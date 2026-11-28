using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

public sealed partial class VocabularyLearningService
{
    private static int ReviewRequired(VocabPlanDay day) => day.Tasks is null or { LegacyDay: true } ? day.DueInScope.Count : VocabDailyTasks.WordCount(day.DueInScope);
    private static int ReviewCompleted(VocabPlanDay day) => day.Tasks is null or { LegacyDay: true } ? day.ReviewedInScope.Count : VocabDailyTasks.CompletedWords(day.DueInScope, day.ReviewedInScope);
    private static int LearningRequired(VocabPlanDay day) => day.Tasks is null or { LegacyDay: true } ? day.NewWordsTarget : day.Tasks.NewRequired + VocabDailyTasks.WordCount(day.Tasks.FillReviews);
    private static int LearningCompleted(VocabPlanDay day) => day.Tasks is null or { LegacyDay: true } ? day.NewWordsLearned :
        Math.Min(day.NewWordsLearned, day.Tasks.NewRequired) + VocabDailyTasks.CompletedWords(day.Tasks.FillReviews, day.Tasks.FillReviewed);

    private async Task<(VocabPlanDay Day, bool Exists, bool Changed)> PrepareDayAsync(
        VocabPlan plan, DateOnly date, bool tolerateCatalogFailure, CancellationToken cancellationToken, string? answeredNewWord = null)
    {
        var day = await _planDayRepository.FindAsync(plan.Id, date, cancellationToken).ConfigureAwait(false);
        var exists = day is not null;
        if (day is not null && (day.Tasks is not null || day.CompletedAt is not null)) return (day, true, false);
        var now = DateTimeOffset.UtcNow;
        day ??= new VocabPlanDay
        {
            PlanId = plan.Id,
            LocalDate = date,
            NewWordsTarget = plan.DailyNewWords,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var tasks = new VocabDailyTasks();
        try
        {
            var planScope = await ReadScopeAsync(plan, cancellationToken).ConfigureAwait(false);
            // One notebook read gives both the new-word sources and the paused cards, so Lexarbor is asked once.
            var notebook = await ReadNotebookAsync(plan.StudentId, cancellationToken).ConfigureAwait(false);
            var scope = WithNotebook(planScope, notebook.NewWords);
            var keys = scope.Select(item => new CardKey(item.Entry.NormalizedWord, item.Entry.MeaningKey)).ToHashSet();
            var existing = await _cardRepository.FindExistingKeysAsync(plan.StudentId, keys, cancellationToken).ConfigureAwait(false);
            var newWords = scope.Where(item => (!existing.Contains(new(item.Entry.NormalizedWord, item.Entry.MeaningKey)) || item.Entry.NormalizedWord == answeredNewWord))
                .GroupBy(item => item.Entry.NormalizedWord, StringComparer.Ordinal).ToList();
            var held = await _questionRepository.FindPendingNewWordKeysAsync(plan.StudentId,
                newWords.Select(word => word.Key).ToList(), now, plan.Id, cancellationToken).ConfigureAwait(false);
            var selected = SelectNewWords(newWords.Where(word => !held.Contains(word.Key)).ToList(),
                Math.Max(0, day.NewWordsTarget - day.NewWordsLearned), day.NewWordsTarget);
            tasks.NewWords = selected.SelectMany(SnapshotNewWord).ToList();
            tasks.NewRequired = Math.Min(day.NewWordsTarget, day.NewWordsLearned + selected.Count);
            day.NewWordsExhausted = newWords.Count == 0;

            // All learned cards are eligible, ordered by due time: due first, then early reviews.
            // Reserve the independent review group first; never repeat a word across groups.
            var newWordKeys = tasks.NewKeys.Select(key => key.WordKey).ToHashSet(StringComparer.Ordinal);
            var due = day.ReviewedInScope.Concat(day.DueInScope).Where(key => !newWordKeys.Contains(key.WordKey)).ToList();
            // Cards keyed by a pending notebook item wait for approval and are not scheduled.
            var paused = notebook.PausedKeys;
            var scanned = new List<CardKey>();
            MemoryCardCursor? cursor = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await _cardRepository.GetDuePageAsync(plan.StudentId, DateTimeOffset.MaxValue, cursor, ReviewScanLimit, cancellationToken).ConfigureAwait(false);
                if (page.Count == 0) break;
                scanned.AddRange(page.Select(card => new CardKey(card.WordKey, card.MeaningKey))
                    .Where(key => !newWordKeys.Contains(key.WordKey) && !paused.Contains(key)));
                var last = page[^1];
                cursor = new(last.DueAt, last.WordKey, last.MeaningKey);
            }
            // A word still being learned as new anywhere (an open new or extra question for another
            // meaning) is not reviewed the same day, so it never asks for spelling mid-learning.
            var learning = await _questionRepository.FindPendingNewWordKeysAsync(plan.StudentId,
                scanned.Select(key => key.WordKey).Distinct(StringComparer.Ordinal).ToList(), now, cancellationToken: cancellationToken).ConfigureAwait(false);
            due.AddRange(scanned.Where(key => !learning.Contains(key.WordKey)));
            var words = due.Distinct().GroupBy(key => key.WordKey, StringComparer.Ordinal).ToList();
            day.DueInScope = words.Take(day.NewWordsTarget).SelectMany(word => word).ToList();
            tasks.FillReviews = words.Skip(day.NewWordsTarget).Take(day.NewWordsTarget - tasks.NewRequired).SelectMany(word => word).ToList();
            // Legacy reviews may already satisfy both groups. Never make the student repeat saved work.
            tasks.FillReviewed = tasks.FillReviews.Intersect(day.ReviewedInScope).ToList();
        }
        catch (VocabCatalogUnavailableException) when (tolerateCatalogFailure)
        {
            // Keep legacy progress judgeable and retry conversion after catalog recovery.
            // A partially constructed snapshot would freeze incomplete requirements permanently.
            return (day, exists, !exists);
        }
        day.Tasks = tasks;
        return (day, exists, true);
    }

    private async Task SaveDailyAsync(VocabPlanDay day, bool exists, CancellationToken cancellationToken)
    {
        day.UpdatedAt = DateTimeOffset.UtcNow;
        if (IsDayComplete(day) && day.CompletedAt is null) day.CompletedAt = day.UpdatedAt;
        if (exists) await _planDayRepository.UpdateAsync(day, cancellationToken).ConfigureAwait(false);
        else await _planDayRepository.AddAsync(day, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<VocabNextQuestionOutcome> GetNextDailyAsync(VocabPlan plan, VocabQuestionMode mode, DateOnly date, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            var (day, exists, _) = await PrepareDayAsync(plan, date, false, cancellationToken).ConfigureAwait(false);
            if (day.Tasks is not { LegacyDay: false } tasks)
                return VocabNextQuestionOutcome.Done(mode == VocabQuestionMode.New ? "daily-learning-completed" : "daily-reviews-completed");
            var now = DateTimeOffset.UtcNow;
            var reviewKeys = mode == VocabQuestionMode.New ? tasks.FillReviews : day.DueInScope;
            var reviewed = mode == VocabQuestionMode.New ? tasks.FillReviewed : day.ReviewedInScope;
            var servingNew = mode == VocabQuestionMode.New && day.NewWordsLearned < tasks.NewRequired;
            var pendingKeys = servingNew ? tasks.NewKeys : reviewKeys.Except(reviewed).ToList();
            var pending = await _questionRepository.FindPendingDailyAsync(plan.Id,
                servingNew ? VocabQuestionKind.New : VocabQuestionKind.Review, pendingKeys, now, cancellationToken).ConfigureAwait(false);
            // A resumed extra review may target the same card on the following day. Keep the
            // assigned review's own pending question and credit each answer to its issuing queue.
            var excluded = new HashSet<Guid>();
            while (!servingNew && pending is not null && !tasks.QuestionIds.Contains(pending.Id))
            {
                var owner = await _planDayRepository.FindReviewQuestionDayAsync(plan.Id, pending.Id, cancellationToken).ConfigureAwait(false);
                if (owner is null) break;
                excluded.UnionWith(owner.Tasks!.ExtraReviewBatches.SelectMany(b => b.QuestionIds));
                pending = await _questionRepository.FindPendingDailyAsync(plan.Id, VocabQuestionKind.Review,
                    pendingKeys, now, cancellationToken, excluded).ConfigureAwait(false);
            }
            if (servingNew && pending is null)
            {
                var extraPending = await _questionRepository.FindPendingDailyAsync(plan.Id, VocabQuestionKind.Extra, pendingKeys, now, cancellationToken).ConfigureAwait(false);
                if (extraPending is not null && await _planDayRepository.FindExtraBatchDayAsync(plan.Id, extraPending.Id, cancellationToken).ConfigureAwait(false) is not null)
                    pending = extraPending;
            }
            if (pending is not null)
            {
                if (tasks.QuestionIds.Contains(pending.Id)) return VocabNextQuestionOutcome.Next(pending);
                tasks.QuestionIds.Add(pending.Id);
                try { await SaveDailyAsync(day, exists, cancellationToken).ConfigureAwait(false); }
                catch (VocabPlanDayConflictException) when (retry < 4) { _unitOfWork.DiscardChanges(); continue; }
                return VocabNextQuestionOutcome.Next(pending);
            }

            var batch = new List<VocabIssuedQuestion>();
            if (mode == VocabQuestionMode.New && day.NewWordsLearned < tasks.NewRequired)
            {
                batch = await PickNewWordQuestionsAsync(plan, VocabQuestionKind.New, tasks.NewWords, tasks.UnavailableNewMeanings,
                    tasks.LearnedWords, day.DueInScope.Concat(tasks.FillReviews), tasks.NewRequired - day.NewWordsLearned,
                    day.NewWordsTarget, now, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0) tasks.NewRequired = Math.Min(tasks.NewRequired, day.NewWordsLearned);
            }
            if (batch.Count == 0)
                batch = await CreateDailyReviewsAsync(plan, reviewKeys, reviewed, now, cancellationToken).ConfigureAwait(false);
            tasks.QuestionIds.AddRange(batch.Select(question => question.Id));
            int? totalWords = null;
            if (batch.Count == 0 && mode == VocabQuestionMode.New && tasks.NewRequired < day.NewWordsTarget)
            {
                var scope = await ReadScopeAsync(plan, cancellationToken).ConfigureAwait(false);
                var keys = scope.Select(item => new CardKey(item.Entry.NormalizedWord, item.Entry.MeaningKey)).ToHashSet();
                var existing = await _cardRepository.FindExistingKeysAsync(plan.StudentId, keys, cancellationToken).ConfigureAwait(false);
                if (existing.Count >= keys.Count)
                {
                    day.NewWordsExhausted = !await HasUnlearnedNotebookMeaningsAsync(plan.StudentId, cancellationToken).ConfigureAwait(false);
                    totalWords = scope.Select(item => item.Entry.NormalizedWord).Distinct(StringComparer.Ordinal).Count();
                }
            }
            try
            {
                await _questionRepository.AddRangeAsync(batch, cancellationToken).ConfigureAwait(false);
                await SaveDailyAsync(day, exists, cancellationToken).ConfigureAwait(false);
                if (batch.Count > 0) return VocabNextQuestionOutcome.Next(batch[0]);
                if (totalWords is { } count)
                    await _planService.RefreshTotalWordsAsync(plan.Id, plan.Version, count, cancellationToken).ConfigureAwait(false);
                return VocabNextQuestionOutcome.Done(mode == VocabQuestionMode.New
                    ? tasks.FillReviews.Count == 0 && day.NewWordsExhausted ? "scope-exhausted" : "daily-learning-completed"
                    : "daily-reviews-completed");
            }
            catch (VocabPlanDayConflictException) when (retry < 4) { _unitOfWork.DiscardChanges(); }
        }
    }

    private async Task<List<VocabIssuedQuestion>> CreateDailyReviewsAsync(VocabPlan plan, List<CardKey> required,
        List<CardKey> completed, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unavailable = new List<CardKey>();
        foreach (var word in required.Where(key => !completed.Contains(key)).GroupBy(key => key.WordKey, StringComparer.Ordinal).ToList())
        {
            var batch = new List<VocabIssuedQuestion>();
            foreach (var key in word)
            {
                var card = await _cardRepository.FindAsync(plan.StudentId, key.WordKey, key.MeaningKey, cancellationToken).ConfigureAwait(false);
                var question = await CreateReviewQuestionAsync(plan.StudentId, plan.Id, key, card?.State, now, cancellationToken).ConfigureAwait(false);
                if (question is null) unavailable.Add(key);
                else batch.Add(question);
            }
            if (batch.Count > 0)
            {
                required.RemoveAll(unavailable.Contains);
                return batch;
            }
        }
        required.RemoveAll(unavailable.Contains);
        return [];
    }
}
