using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

public sealed partial class VocabularyLearningService
{
    private async Task<VocabNextQuestionOutcome> GetNextExtraReviewAsync(VocabPlan plan, DateOnly date,
        Guid? requestedId, CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            var today = await _planDayRepository.FindAsync(plan.Id, date, cancellationToken).ConfigureAwait(false);
            if (today?.CompletedAt is null) throw new VocabDayNotCompletedException();
            var requestedDay = requestedId is { } id
                ? await _planDayRepository.FindReviewBatchDayAsync(plan.Id, id, cancellationToken).ConfigureAwait(false) : null;
            var owner = requestedDay ?? await _planDayRepository.FindReviewBatchDayAsync(plan.Id, null, cancellationToken).ConfigureAwait(false) ?? today;
            owner.Tasks ??= new VocabDailyTasks { LegacyDay = true };
            var batches = owner.Tasks.ExtraReviewBatches;
            var batch = batches.FirstOrDefault(b => b.Id == requestedId || requestedId is { } alias && b.RequestIds.Contains(alias))
                ?? batches.LastOrDefault(b => b.CompletedAt is null);
            if (requestedId is null) batch ??= batches.LastOrDefault();
            if (batch?.CompletedAt is not null) return ReviewBatchDone(batch);

            var now = DateTimeOffset.UtcNow;
            var paused = await ReadPausedCardKeysAsync(plan.StudentId, cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                batch = new VocabReviewBatch { Id = requestedId ?? Guid.NewGuid(), Target = plan.DailyNewWords * 2 };
                await FillReviewBatchAsync(plan.StudentId, batch, paused, now, cancellationToken).ConfigureAwait(false);
                batches.Add(batch);
            }
            if (requestedId is { } request && request != batch.Id && !batch.RequestIds.Contains(request)) batch.RequestIds.Add(request);
            var question = await NextBatchReviewQuestionAsync(plan, batch, paused, now, cancellationToken).ConfigureAwait(false);
            if (question is null) batch.CompletedAt = now;
            try
            {
                if (question is not null && !batch.QuestionIds.Contains(question.Id))
                {
                    batch.QuestionIds.Add(question.Id);
                    await _questionRepository.AddRangeAsync([question], cancellationToken).ConfigureAwait(false);
                }
                await SaveDailyAsync(owner, true, cancellationToken).ConfigureAwait(false);
                return question is null ? ReviewBatchDone(batch) : VocabNextQuestionOutcome.Next(question, batch.Snapshot());
            }
            catch (VocabPlanDayConflictException) when (retry < 4) { _unitOfWork.DiscardChanges(); }
        }
    }

    private static VocabNextQuestionOutcome ReviewBatchDone(VocabReviewBatch batch) =>
        VocabNextQuestionOutcome.Done(batch.Completed == 0 ? "no-reviewable-words" : "extra-batch-completed", batch.Snapshot());

    private async Task FillReviewBatchAsync(Guid studentId, VocabReviewBatch batch, IReadOnlySet<CardKey> paused,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var words = batch.Cards.Select(key => key.WordKey).ToHashSet(StringComparer.Ordinal);
        var skipped = batch.Unavailable.ToHashSet();
        MemoryCardCursor? cursor = null;
        while (batch.Cards.Count < batch.Target)
        {
            var page = await _cardRepository.GetDuePageAsync(studentId, DateTimeOffset.MaxValue, cursor, ReviewScanLimit, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            var pending = await _questionRepository.FindPendingNewWordKeysAsync(studentId,
                page.Select(card => card.WordKey).Distinct().ToList(), now, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var card in page)
            {
                var key = new CardKey(card.WordKey, card.MeaningKey);
                if (paused.Contains(key) || skipped.Contains(key) || pending.Contains(key.WordKey) || !words.Add(key.WordKey)) continue;
                batch.Cards.Add(key);
                if (batch.Cards.Count == batch.Target) break;
            }
            var last = page[^1];
            cursor = new(last.DueAt, last.WordKey, last.MeaningKey);
        }
    }

    private async Task<VocabIssuedQuestion?> NextBatchReviewQuestionAsync(VocabPlan plan, VocabReviewBatch batch,
        IReadOnlySet<CardKey> paused, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = batch.QuestionIds.Count == 0 ? null :
            await _questionRepository.FindByIdAsync(batch.QuestionIds[^1], cancellationToken).ConfigureAwait(false);
        while (batch.Completed < batch.Cards.Count)
        {
            var key = batch.Cards[batch.Completed];
            if (!paused.Contains(key))
            {
                if (pending is { AnsweredAt: null } && pending.ExpiresAt > now && pending.WordKey == key.WordKey && pending.MeaningKey == key.MeaningKey)
                    return pending;
                var card = await _cardRepository.FindAsync(plan.StudentId, key.WordKey, key.MeaningKey, cancellationToken).ConfigureAwait(false);
                if (card is not null)
                {
                    var question = await CreateReviewQuestionAsync(plan.StudentId, plan.Id, key, card.State, now, cancellationToken).ConfigureAwait(false);
                    if (question is not null) return question;
                }
            }
            batch.Cards.RemoveAt(batch.Completed);
            batch.Unavailable.Add(key);
            await FillReviewBatchAsync(plan.StudentId, batch, paused, now, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<bool> RecordExtraReviewAsync(Guid planId, VocabIssuedQuestion question, DateTimeOffset answeredAt,
        CancellationToken cancellationToken)
    {
        var owner = await _planDayRepository.FindReviewQuestionDayAsync(planId, question.Id, cancellationToken).ConfigureAwait(false);
        if (owner is null) return false;
        var batch = owner.Tasks!.ExtraReviewBatches.Single(b => b.QuestionIds.Contains(question.Id));
        if (batch.CompletedAt is null && batch.QuestionIds[^1] == question.Id)
        {
            batch.Completed++;
            if (batch.Completed == batch.Cards.Count) batch.CompletedAt = answeredAt;
            await SaveDailyAsync(owner, true, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }
}
