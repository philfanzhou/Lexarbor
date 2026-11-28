using System.Text;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lexarbor.Learning.Domain.Services;

public enum VocabQuestionMode
{
    New,
    Review,
    Extra,
    ExtraReview,
}
public sealed partial class VocabularyLearningService
{

    private const int WordChunkSize = 16;

    /// <summary>Due-card page size; the cursor advances through all pages.</summary>
    private const int ReviewScanLimit = 100;

    private readonly ILearnerDirectory _studentRepository;
    private readonly IVocabPlanRepository _planRepository;
    private readonly IVocabPlanDayRepository _planDayRepository;
    private readonly IVocabIssuedQuestionRepository _questionRepository;
    private readonly IVocabAttemptRepository _attemptRepository;
    private readonly IMemoryCardRepository _cardRepository;
    private readonly IVocabCardLocationRepository _cardLocationRepository;
    private readonly VocabularyMemoryService _memoryService;
    private readonly VocabularyPlanService _planService;
    private readonly IVocabularyScopeEntryReader _scopeEntryReader;
    private readonly IVocabularyQuestionGenerator _questionGenerator;
    private readonly IUnitOfWork _unitOfWork;
    private readonly VocabularyLearningOptions _options;
    private readonly ILogger<VocabularyLearningService>? _logger;
    private readonly IVocabularyNotebookSource? _notebookSource;

    public VocabularyLearningService(
        ILearnerDirectory studentRepository,
        IVocabPlanRepository planRepository,
        IVocabPlanDayRepository planDayRepository,
        IVocabIssuedQuestionRepository questionRepository,
        IVocabAttemptRepository attemptRepository,
        IMemoryCardRepository cardRepository,
        IVocabCardLocationRepository cardLocationRepository,
        VocabularyMemoryService memoryService,
        VocabularyPlanService planService,
        IVocabularyScopeEntryReader scopeEntryReader,
        IVocabularyQuestionGenerator questionGenerator,
        IUnitOfWork unitOfWork,
        IOptions<VocabularyLearningOptions> options,
        ILogger<VocabularyLearningService>? logger = null,
        IVocabularyNotebookSource? notebookSource = null)
    {
        _notebookSource = notebookSource;
        _studentRepository = studentRepository;
        _planRepository = planRepository;
        _planDayRepository = planDayRepository;
        _questionRepository = questionRepository;
        _attemptRepository = attemptRepository;
        _cardRepository = cardRepository;
        _cardLocationRepository = cardLocationRepository;
        _memoryService = memoryService;
        _planService = planService;
        _scopeEntryReader = scopeEntryReader;
        _questionGenerator = questionGenerator;
        _unitOfWork = unitOfWork;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VocabTodaySnapshot> GetTodayAsync(Guid studentId, DateOnly localDate, CancellationToken cancellationToken = default)
    {
        var plan = await RequireActivePlanAsync(studentId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var day = await GetOrCreateDayAsync(plan, localDate, tolerateCatalogFailure: false, cancellationToken).ConfigureAwait(false);
        await TryCompleteDayAsync(day, cancellationToken).ConfigureAwait(false);

        var allDue = await _cardRepository.CountDueAsync(studentId, now, cancellationToken).ConfigureAwait(false);
        var teacherAdjustedAt = await _planService.GetTeacherAdjustedAtAsync(plan.Id, cancellationToken).ConfigureAwait(false);
        var activeExtraDay = await _planDayRepository.FindExtraBatchDayAsync(plan.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        var extraBatch = activeExtraDay?.Tasks?.ExtraBatches.LastOrDefault(batch => batch.CompletedAt is null)
            ?? day.Tasks?.ExtraBatches.LastOrDefault();
        var reviewBatch = await _planDayRepository.FindLatestReviewBatchAsync(plan.Id, cancellationToken).ConfigureAwait(false);
        var exhausted = day.NewWordsExhausted && !await HasUnlearnedNotebookMeaningsAsync(studentId, cancellationToken).ConfigureAwait(false);
        return new VocabTodaySnapshot(
            plan.Id,
            localDate,
            day.NewWordsTarget,
            day.NewWordsLearned,
            ReviewRequired(day),
            ReviewCompleted(day),
            allDue,
            day.CompletedAt,
            day.CompletedAt is not null,
            teacherAdjustedAt,
            exhausted,
            LearningRequired(day),
            LearningCompleted(day),
            day.Tasks is null ? 0 : VocabDailyTasks.WordCount(day.Tasks.FillReviews),
            day.Tasks is null ? 0 : VocabDailyTasks.CompletedWords(day.Tasks.FillReviews, day.Tasks.FillReviewed),
            plan.DailyNewWords,
            extraBatch?.Snapshot(), plan.DailyNewWords * 2, reviewBatch);
    }

    public async Task<VocabNextQuestionOutcome> GetNextAsync(
        Guid studentId, VocabQuestionMode mode, DateOnly localDate, CancellationToken cancellationToken = default, Guid? extraBatchId = null)
    {
        var plan = await RequireActivePlanAsync(studentId, cancellationToken).ConfigureAwait(false);
        if (mode is VocabQuestionMode.New or VocabQuestionMode.Review)
            return await GetNextDailyAsync(plan, mode, localDate, cancellationToken).ConfigureAwait(false);
        if (mode == VocabQuestionMode.ExtraReview)
            return await GetNextExtraReviewAsync(plan, localDate, extraBatchId, cancellationToken).ConfigureAwait(false);
        return await GetNextExtraAsync(plan, localDate, extraBatchId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VocabFreeReviewSnapshot> GetFreeReviewsAsync(Guid studentId, DateOnly localDate, CancellationToken cancellationToken = default)
    {
        await RequireNoActivePlanAsync(studentId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var count = await _cardRepository.CountDueAsync(studentId, now, cancellationToken).ConfigureAwait(false);
        return new VocabFreeReviewSnapshot(studentId, localDate, count);
    }

    public async Task<VocabNextQuestionOutcome> GetNextFreeReviewAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        await RequireNoActivePlanAsync(studentId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var pending = await _questionRepository.FindPendingByKindAsync(studentId, VocabQuestionKind.Review, true, now, cancellationToken).ConfigureAwait(false);
        return pending is not null ? VocabNextQuestionOutcome.Next(pending)
            : await ServeNextReviewAsync(studentId, null, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireNoActivePlanAsync(Guid studentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _studentRepository.ExistsAsync(studentId, cancellationToken).ConfigureAwait(false)) throw new VocabStudentNotFoundException();
        cancellationToken.ThrowIfCancellationRequested();
        if (await _planRepository.FindActiveByStudentAsync(studentId, cancellationToken).ConfigureAwait(false) is not null)
            throw new VocabPlanActiveExistsException();
    }

    public async Task<VocabAnswerResult> AnswerAsync(
        Guid questionId,
        Guid studentId,
        Guid? optionId,
        bool unknown,
        int elapsedMs,
        DateOnly localDate,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? letters = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var question = await _questionRepository.FindByIdAsync(questionId, cancellationToken).ConfigureAwait(false);
        if (question is null || question.StudentId != studentId)
        {
            throw new VocabQuestionNotFoundException(questionId);
        }

        // PostgreSQL persists microseconds. A first response and its saved replay must agree.
        var instant = DateTimeOffset.UtcNow;
        var now = instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMicrosecond));
        if (question.AnsweredAt is null && now > question.ExpiresAt)
        {
            throw new VocabQuestionExpiredException();
        }

        var fillLetters = question.Direction.IsSpelling();
        if (fillLetters)
        {
            if (optionId is not null || (unknown && letters is not null) || (!unknown && letters is null))
                throw new VocabLettersInvalidException();
        }
        else if (letters is not null)
        {
            throw new VocabLettersInvalidException();
        }
        else if (unknown)
        {
            if (optionId is not null)
            {
                throw new VocabOptionInvalidException();
            }
        }
        else if (optionId is null)
        {
            throw new VocabOptionInvalidException();
        }

        var isCorrect = !unknown && (fillLetters
            ? JudgeLetters(question, letters!)
            : optionId == question.CorrectOptionId);
        // Validate the new request before replay without changing the saved first answer.
        if (question.AnsweredAt is not null)
        {
            return await LoadFirstResultAsync(question, cancellationToken).ConfigureAwait(false);
        }
        if (!fillLetters && !unknown && question.Options.All(option => option.OptionId != optionId))
        {
            throw new VocabOptionInvalidException();
        }

        var serverElapsedMs = (long)Math.Max(0, (now - question.IssuedAt).TotalMilliseconds);
        var effectiveElapsedMs = (int)Math.Clamp(Math.Max(elapsedMs, serverElapsedMs), 0, int.MaxValue);
        var result = unknown ? VocabAttemptResult.Unknown : isCorrect ? VocabAttemptResult.Correct : VocabAttemptResult.Wrong;
        var slowAnswerMs = fillLetters ? _options.FillLettersSlowAnswerMs : _options.SlowAnswerMs;
        var rating = isCorrect
            ? (effectiveElapsedMs > slowAnswerMs ? ReviewRating.Hard : ReviewRating.Good)
            : ReviewRating.Again;

        var attempt = new VocabAttempt
        {
            IssuedQuestionId = question.Id,
            Result = result,
            SelectedOptionId = optionId,
            FilledLetters = fillLetters && !unknown ? letters!.ToList() : null,
            ElapsedMs = effectiveElapsedMs,
            Rating = rating,
            AnsweredAt = now,
            LocalDate = localDate,
        };

        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await _unitOfWork.InTransactionAsync(async () =>
                {
                    if (question.Kind is VocabQuestionKind.New or VocabQuestionKind.Extra)
                        await _questionRepository.LockNewWordAnswersAsync(studentId, question.WordKey, cancellationToken).ConfigureAwait(false);
                    // Claim the answer before applying the rating. Every save is part of this transaction.
                    await _questionRepository.MarkAnsweredAsync(question.Id, now, cancellationToken).ConfigureAwait(false);
                    await _attemptRepository.AddAsync(attempt, cancellationToken).ConfigureAwait(false);
                    await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    var (card, created) = await _memoryService.RecordAsync(
                        studentId, question.WordKey, question.MeaningKey, rating, now, question.PlanId, cancellationToken).ConfigureAwait(false);
                    if (created && question.Kind is (VocabQuestionKind.New or VocabQuestionKind.Extra))
                    {
                        // The card is reviewed where it was learned; later questions never move it (#443).
                        await _cardLocationRepository.AddAsync(new VocabCardLocation(studentId,
                            new CardKey(question.WordKey, question.MeaningKey), question.BookId, question.WordId, question.MeaningId,
                            question.Source, question.Id, now), cancellationToken).ConfigureAwait(false);
                        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    await ApplyDayProgressAsync(question, attempt, cancellationToken).ConfigureAwait(false);
                    return new VocabAnswerResult(question.Id, result, question.CorrectOptionId, rating, card.DueAt,
                        fillLetters ? question.AnswerWord : null);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (VocabAttemptDuplicateException)
            {
                return await LoadFirstResultAsync(question, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (retry < 4 && error is VocabPlanDayConflictException or MemoryCardConflictException)
            {
                // Replay from a fresh database state after the entire answer has rolled back.
            }
        }
    }

    private static bool JudgeLetters(VocabIssuedQuestion question, IReadOnlyList<string> letters)
    {
        var pattern = question.LetterPattern;
        var answer = question.AnswerWord?.EnumerateRunes().ToArray();
        if (pattern is null || answer is null || pattern.Count != answer.Length
            || letters.Count == 0 || letters.Count != pattern.Count(value => value is null))
            throw new VocabLettersInvalidException();

        var correct = true;
        var index = 0;
        for (var position = 0; position < pattern.Count; position++)
        {
            if (pattern[position] is not null) continue;
            var value = letters[index++];
            if (string.IsNullOrEmpty(value) || !Rune.TryGetRuneAt(value, 0, out var submitted)
                || submitted.Utf16SequenceLength != value.Length || !Rune.IsLetter(submitted))
                throw new VocabLettersInvalidException();
            correct &= Rune.ToUpperInvariant(submitted) == Rune.ToUpperInvariant(answer[position]);
        }
        return correct;
    }


    private async Task<VocabPlan> RequireActivePlanAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var plan = await _planRepository.FindActiveByStudentAsync(studentId, cancellationToken).ConfigureAwait(false)
            ?? throw VocabPlanNotFoundException.NoActivePlan();
        return plan;
    }

    /// <summary>
    /// Read or create the two fixed daily lists, preserving completed legacy days.
    /// During answer handling, catalog failure defers legacy conversion without freezing an incomplete list.
    /// Other paths propagate catalog failures without writing a new daily row.
    /// </summary>
    private async Task<VocabPlanDay> GetOrCreateDayAsync(
        VocabPlan plan, DateOnly localDate, bool tolerateCatalogFailure, CancellationToken cancellationToken, string? answeredNewWord = null)
    {
        for (var retry = 0; ; retry++)
        {
            var (day, exists, changed) = await PrepareDayAsync(plan, localDate, tolerateCatalogFailure, cancellationToken, answeredNewWord).ConfigureAwait(false);
            if (!changed) return day;
            try
            {
                await SaveDailyAsync(day, exists, cancellationToken).ConfigureAwait(false);
                return day;
            }
            catch (VocabPlanDayConflictException) when (retry < 4) { _unitOfWork.DiscardChanges(); }
        }
    }

    private async Task TryCompleteDayAsync(VocabPlanDay day, CancellationToken cancellationToken)
    {
        if (day.CompletedAt is not null || !IsDayComplete(day))
        {
            return;
        }

        day.CompletedAt = DateTimeOffset.UtcNow;
        await UpdateDayWithRetryAsync(day, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Complete a nonempty day when both independent groups have been answered.
    /// Legacy checked-in rows retain their original facts.
    /// </summary>
    private static bool IsDayComplete(VocabPlanDay day) => day.Tasks is null or { LegacyDay: true }
        ? (day.NewWordsLearned >= 1 || day.DueInScope.Count > 0) &&
          (day.NewWordsLearned >= day.NewWordsTarget || day.NewWordsExhausted) &&
          day.DueInScope.All(day.ReviewedInScope.Contains)
        : (LearningRequired(day) > 0 || ReviewRequired(day) > 0) &&
          LearningCompleted(day) >= LearningRequired(day) && ReviewCompleted(day) >= ReviewRequired(day);
    private async Task MarkNewWordsExhaustedAsync(VocabPlan plan, DateOnly localDate, CancellationToken cancellationToken)
    {
        for (var attemptIndex = 0; ; attemptIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var day = await GetOrCreateDayAsync(plan, localDate, tolerateCatalogFailure: false, cancellationToken).ConfigureAwait(false);
            var current = await _planRepository.FindByIdAsync(plan.Id, cancellationToken).ConfigureAwait(false);
            if (current is null || current.Version != plan.Version)
            {
                return;
            }

            var changed = !day.NewWordsExhausted;
            day.NewWordsExhausted = true;
            if (day.CompletedAt is null && IsDayComplete(day))
            {
                day.CompletedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
            if (!changed)
            {
                return;
            }

            try
            {
                await _planDayRepository.UpdateAsync(day, cancellationToken).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (VocabPlanDayConflictException) when (attemptIndex < 4)
            {
                _unitOfWork.DiscardChanges();
            }
        }
    }

    private async Task ApplyDayProgressAsync(VocabIssuedQuestion question, VocabAttempt attempt, CancellationToken cancellationToken)
    {
        if (question.PlanId is not { } planId) return;
        if (question.Kind == VocabQuestionKind.Review && await RecordExtraReviewAsync(planId, question, attempt.AnsweredAt, cancellationToken).ConfigureAwait(false)) return;
        var day = await _planDayRepository.FindAsync(planId, attempt.LocalDate, cancellationToken).ConfigureAwait(false);
        var exists = day is not null;
        var learning = question.Kind is VocabQuestionKind.New or VocabQuestionKind.Extra;
        if (day is null or { Tasks: null, CompletedAt: null })
        {
            var plan = await _planRepository.FindByIdAsync(planId, cancellationToken).ConfigureAwait(false);
            if (plan is not null)
            {
                (day, exists, _) = await PrepareDayAsync(plan, attempt.LocalDate, tolerateCatalogFailure: true,
                    cancellationToken, learning ? question.WordKey : null).ConfigureAwait(false);
            }
        }
        if (day is null) return;

        if (learning)
        {
            var stillPending = await _questionRepository.HasPendingForWordAsync(
                question.StudentId, question.WordKey, attempt.AnsweredAt, cancellationToken).ConfigureAwait(false);
            if (!stillPending && (day.Tasks is null || !day.Tasks.LearnedWords.Contains(question.WordKey, StringComparer.Ordinal)))
            {
                day.NewWordsLearned += 1;
                day.Tasks?.LearnedWords.Add(question.WordKey);
            }
            if (!stillPending)
                await ApplyExtraProgressAsync(day, question, cancellationToken).ConfigureAwait(false);
        }
        else if (question.Kind == VocabQuestionKind.Review)
        {
            var key = new CardKey(question.WordKey, question.MeaningKey);
            if (day.DueInScope.Contains(key) && !day.ReviewedInScope.Contains(key))
                day.ReviewedInScope.Add(key);
            if (day.Tasks is { } tasks && tasks.FillReviews.Contains(key) && !tasks.FillReviewed.Contains(key))
                tasks.FillReviewed.Add(key);
        }

        await SaveDailyAsync(day, exists, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateDayWithRetryAsync(VocabPlanDay day, CancellationToken cancellationToken)
    {
        for (var attemptIndex = 0; ; attemptIndex++)
        {
            try
            {
                await _planDayRepository.UpdateAsync(day, cancellationToken).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (VocabPlanDayConflictException) when (attemptIndex < 4)
            {
                _unitOfWork.DiscardChanges();
                var fresh = await _planDayRepository.FindAsync(day.PlanId, day.LocalDate, cancellationToken).ConfigureAwait(false);
                if (fresh is null)
                {
                    return;
                }

                fresh.ReviewedInScope = fresh.ReviewedInScope.Union(day.ReviewedInScope).ToList();
                if (day.CompletedAt is not null && fresh.CompletedAt is null && IsDayComplete(fresh))
                {
                    fresh.CompletedAt = day.CompletedAt;
                }
                day = fresh;
            }
        }
    }


    private Task<IReadOnlyList<VocabularyPlanScopeEntry>> ReadScopeAsync(VocabPlan plan, CancellationToken cancellationToken) =>
        VocabularyPlanScopeEntry.ReadAsync(_scopeEntryReader, plan.Segments, cancellationToken);
    private async Task<IReadOnlyList<VocabularyPlanScopeEntry>> WithNotebookAsync(
        VocabPlan plan, IReadOnlyList<VocabularyPlanScopeEntry> scope, CancellationToken cancellationToken,
        IReadOnlySet<VocabDailyWord>? unavailable = null)
    {
        var notebook = _notebookSource is null ? [] :
            await _notebookSource.ReadNewWordSourcesAsync(plan.StudentId, cancellationToken).ConfigureAwait(false);
        return WithNotebook(scope, notebook, unavailable);
    }

    private static IReadOnlyList<VocabularyPlanScopeEntry> WithNotebook(
        IReadOnlyList<VocabularyPlanScopeEntry> scope, IReadOnlyList<VocabularyPlanScopeEntry> notebook,
        IReadOnlySet<VocabDailyWord>? unavailable = null)
    {
        if (notebook.Count == 0 && unavailable is null) return scope;
        var planned = new Dictionary<CardKey, VocabularyPlanScopeEntry>();
        foreach (var item in scope) planned.TryAdd(ScopeKey(item), item);
        var seen = new HashSet<CardKey>();
        // A notebook meaning the plan also covers keeps its notebook priority but is learned at the plan's
        // location, so its question keeps the plan's book and unit. Its notebook location takes the place only
        // once the plan location is unavailable (IssueFromRosterAsync also tries it directly).
        return notebook.OrderBy(item => item.Source == VocabularySourceKind.NotebookFamily)
            .SelectMany(item => planned.TryGetValue(ScopeKey(item), out var plannedItem)
                ? [plannedItem with { Source = item.Source }, item with { CoveredByPlan = true }]
                : new[] { item with { CoveredByPlan = false } })
            .Concat(scope)
            .Where(item => unavailable?.Contains(ToDailyWord(item)) != true)
            .Where(item => seen.Add(ScopeKey(item)))
            .ToList();
    }

    private static CardKey ScopeKey(VocabularyPlanScopeEntry item) => new(item.Entry.NormalizedWord, item.Entry.MeaningKey);

    private async Task<VocabNotebookLearningSources> ReadNotebookAsync(Guid studentId, CancellationToken cancellationToken) =>
        _notebookSource is null ? new([], new HashSet<CardKey>())
            : await _notebookSource.ReadLearningSourcesAsync(studentId, cancellationToken).ConfigureAwait(false);

    private async Task<bool> HasUnlearnedNotebookMeaningsAsync(Guid studentId, CancellationToken cancellationToken)
    {
        if (_notebookSource is null) return false;
        var sources = await _notebookSource.ReadNewWordSourcesAsync(studentId, cancellationToken).ConfigureAwait(false);
        var keys = sources.Select(item => new CardKey(item.Entry.NormalizedWord, item.Entry.MeaningKey)).ToHashSet();
        return keys.Count > 0 &&
            (await _cardRepository.FindExistingKeysAsync(studentId, keys, cancellationToken).ConfigureAwait(false)).Count < keys.Count;
    }

    private static List<IGrouping<string, VocabularyPlanScopeEntry>> SelectNewWords(
        IReadOnlyList<IGrouping<string, VocabularyPlanScopeEntry>> eligible, int target, int groupSize)
    {
        var familyRemaining = eligible.Any(word => word.Any(item => item.CoveredByPlan)) ? Math.Max(1, groupSize / 3) : int.MaxValue;
        var selected = new List<IGrouping<string, VocabularyPlanScopeEntry>>();
        foreach (var word in OrderNewWords(eligible))
        {
            if (selected.Count == target) break;
            if (UsesFamilyQuota(word))
            {
                if (familyRemaining == 0) continue;
                familyRemaining--;
            }
            selected.Add(word);
        }
        return selected;
    }

    private static bool ManualNotebookWord(IEnumerable<VocabularyPlanScopeEntry> word) =>
        word.Any(item => item.Source == VocabularySourceKind.Notebook);

    private static bool UsesFamilyQuota(IEnumerable<VocabularyPlanScopeEntry> word) =>
        word.Any(item => item.Source == VocabularySourceKind.NotebookFamily) && !ManualNotebookWord(word) && !word.Any(item => item.CoveredByPlan);

    private static IEnumerable<IGrouping<string, VocabularyPlanScopeEntry>> OrderNewWords(
        IEnumerable<IGrouping<string, VocabularyPlanScopeEntry>> words) =>
        words.OrderBy(word => ManualNotebookWord(word) ? 0 : UsesFamilyQuota(word) ? 1 : 2);

    private static VocabDailyWord ToDailyWord(VocabularyPlanScopeEntry item) =>
        new(item.BookId, item.UnitScoped ? item.Entry.UnitId : null, item.Entry.WordId, item.Entry.NormalizedWord,
            item.Entry.MeaningId, item.Entry.MeaningKey);

    private static IEnumerable<VocabDailyWord> SnapshotNewWord(IGrouping<string, VocabularyPlanScopeEntry> word)
    {
        var usesFamilyQuota = UsesFamilyQuota(word);
        return word.Select(item => ToDailyWord(item) with { UsesFamilyQuota = usesFamilyQuota, Source = item.Source });
    }

    /// <summary>Cards keyed by a pending notebook item wait for approval and are not scheduled (ADR-0027).</summary>
    private async Task<IReadOnlySet<CardKey>> ReadPausedCardKeysAsync(Guid studentId, CancellationToken cancellationToken) =>
        _notebookSource is null ? new HashSet<CardKey>()
            : await _notebookSource.ReadPausedCardKeysAsync(studentId, cancellationToken).ConfigureAwait(false);

    private async Task<List<VocabIssuedQuestion>> PickNewWordQuestionsAsync(
        VocabPlan plan, VocabQuestionKind kind, List<VocabDailyWord> roster, List<VocabDailyWord> unavailable,
        IReadOnlyCollection<string> learnedWords, IEnumerable<CardKey> reviewKeys, int remaining, int groupSize,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var questions = await IssueFromRosterAsync(plan, kind, roster, unavailable, now, cancellationToken).ConfigureAwait(false);
        if (questions.Count > 0 || roster.Select(word => word.WordKey).Distinct(StringComparer.Ordinal)
            .Count(word => !learnedWords.Contains(word)) >= remaining)
        {
            return questions;
        }

        // Refill only the open slots left by unavailable selections. Previously reserved words
        // and both review groups still own their places, including words already answered.
        var scope = await ReadScopeAsync(plan, cancellationToken).ConfigureAwait(false);
        var sources = await WithNotebookAsync(plan, scope, cancellationToken, unavailable.ToHashSet()).ConfigureAwait(false);
        var existing = await _cardRepository.FindExistingKeysAsync(plan.StudentId,
            sources.Select(item => new CardKey(item.Entry.NormalizedWord, item.Entry.MeaningKey)).ToHashSet(),
            cancellationToken).ConfigureAwait(false);
        var unlearned = sources.Where(item => !existing.Contains(new(item.Entry.NormalizedWord, item.Entry.MeaningKey))).ToList();
        var assigned = roster.Select(word => word.WordKey).Concat(learnedWords).Concat(reviewKeys.Select(key => key.WordKey))
            .ToHashSet(StringComparer.Ordinal);
        var familyLimit = unlearned.Any(item => item.CoveredByPlan) ? Math.Max(1, groupSize / 3) : int.MaxValue;
        var familyWords = sources.GroupBy(item => item.Entry.NormalizedWord, StringComparer.Ordinal)
            .Where(UsesFamilyQuota).Select(word => word.Key).ToHashSet(StringComparer.Ordinal);
        var familyUsed = roster.Where(word => word.UsesFamilyQuota ?? familyWords.Contains(word.WordKey))
            .Select(word => word.WordKey).Distinct(StringComparer.Ordinal).Count();
        var candidates = OrderNewWords(unlearned.Where(item => !assigned.Contains(item.Entry.NormalizedWord))
            .GroupBy(item => item.Entry.NormalizedWord, StringComparer.Ordinal)).ToList();
        var reserve = candidates
            .Where(word => familyUsed < familyLimit || !UsesFamilyQuota(word))
            .SelectMany(SnapshotNewWord).ToList();
        questions = await IssueFromRosterAsync(plan, kind, reserve, unavailable, now, cancellationToken).ConfigureAwait(false);
        if (questions.Count == 0 && familyUsed >= familyLimit)
        {
            var failedLocations = unavailable.ToHashSet();
            if (!unlearned.Any(item => item.CoveredByPlan && !failedLocations.Contains(ToDailyWord(item))))
            {
                // The last eligible plan meanings failed during this scan. The family quota no longer applies.
                reserve = candidates.Where(UsesFamilyQuota).SelectMany(SnapshotNewWord).ToList();
                questions = await IssueFromRosterAsync(plan, kind, reserve, unavailable, now, cancellationToken).ConfigureAwait(false);
            }
        }
        if (questions.Count > 0)
            roster.AddRange(reserve.Where(word => word.WordKey == questions[0].WordKey));
        return questions;
    }

    private async Task<List<VocabIssuedQuestion>> IssueFromRosterAsync(
        VocabPlan plan, VocabQuestionKind kind, List<VocabDailyWord> roster, List<VocabDailyWord> unavailable,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unavailableLocations = unavailable.ToHashSet();
        roster.RemoveAll(word => unavailableLocations.Contains(word.LocationOnly()));
        IReadOnlyList<VocabularyPlanScopeEntry>? notebook = null;
        foreach (var chunk in roster.GroupBy(word => word.WordKey, StringComparer.Ordinal).ToList().Chunk(WordChunkSize))
        {
            var keys = chunk.SelectMany(word => word).Select(word => new CardKey(word.WordKey, word.MeaningKey)).ToList();
            var existing = await _cardRepository.FindExistingKeysAsync(plan.StudentId, keys, cancellationToken).ConfigureAwait(false);
            var pending = await _questionRepository.FindPendingNewWordKeysAsync(plan.StudentId, chunk.Select(word => word.Key).ToList(),
                now, cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var word in chunk.Where(word => !pending.Contains(word.Key)))
            {
                var questions = new List<VocabIssuedQuestion>();
                var issuedAt = now;
                foreach (var meaning in word.Where(item => !existing.Contains(new(item.WordKey, item.MeaningKey))))
                {
                    issuedAt = issuedAt.AddTicks(10);
                    var entry = new VocabularyScopeEntry(meaning.WordId, meaning.WordKey, meaning.MeaningId, meaning.MeaningKey, meaning.UnitId);
                    var question = await CreateQuestionAsync(plan.StudentId, plan.Id, meaning.BookId, kind, entry,
                        meaning.UnitId, issuedAt, cancellationToken).ConfigureAwait(false);
                    if (question is null && _notebookSource is not null)
                    {
                        // A notebook meaning the plan also covers is learned at the plan's location; when that
                        // location gives no question, its notebook location still can.
                        notebook ??= await _notebookSource.ReadNewWordSourcesAsync(plan.StudentId, cancellationToken).ConfigureAwait(false);
                        // The word still comes from the notebook: only its location changes.
                        var fallback = notebook.Select(item => ToDailyWord(item) with { UsesFamilyQuota = meaning.UsesFamilyQuota, Source = meaning.Source })
                            .FirstOrDefault(item => item.WordKey == meaning.WordKey && item.MeaningKey == meaning.MeaningKey
                                && item != meaning && !unavailableLocations.Contains(item.LocationOnly()));
                        if (fallback is not null)
                        {
                            question = await CreateQuestionAsync(plan.StudentId, plan.Id, fallback.BookId, kind,
                                new VocabularyScopeEntry(fallback.WordId, fallback.WordKey, fallback.MeaningId, fallback.MeaningKey, null),
                                null, issuedAt, cancellationToken).ConfigureAwait(false);
                            if (question is null) unavailable.Add(fallback.LocationOnly());
                            else roster[roster.IndexOf(meaning)] = fallback;
                        }
                    }
                    if (question is null)
                    {
                        unavailable.Add(meaning.LocationOnly());
                        roster.Remove(meaning);
                    }
                    else
                    {
                        question.Source = meaning.Source;
                        questions.Add(question);
                    }
                }
                if (questions.Count > 0) return questions;
            }
        }
        return [];
    }

    /// <summary>
    /// Requests one question for the exact target. New and Extra ask for new mode. A review asks
    /// for new mode (EnglishToChinese only) while its card has not graduated to the FSRS Review
    /// state, and for review mode afterwards; when a choice question cannot be generated for a
    /// card still being learned, it asks once more in review mode. Lexarbor chooses the type;
    /// Student keeps its own question and option IDs, association and timing.
    /// </summary>
    private async Task<VocabIssuedQuestion?> CreateQuestionAsync(
        Guid studentId,
        Guid? planId,
        string bookId,
        VocabQuestionKind kind,
        VocabularyScopeEntry entry,
        string? unitId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        MemoryCardState? cardState = null)
    {
        if (bookId == VocabNotebook.SubmissionBookId)
        {
            // Learning waits for approval (ADR-0027): a target saved from a meaning submission continues at the
            // approved catalog meaning; while the submission is pending, rejected or changed it gets no question.
            var location = _notebookSource is null ? null : await _notebookSource.ResolveSubmissionCardAsync(
                studentId, new CardKey(entry.NormalizedWord, entry.MeaningKey), entry.MeaningId, cancellationToken).ConfigureAwait(false);
            if (location is null)
            {
                _logger?.LogInformation(
                    "Vocabulary question skipped for submission {SubmissionId}: it is not approved (kind {Kind}, plan {PlanId}).",
                    entry.MeaningId, kind, planId);
                return null;
            }

            bookId = location.BookId;
            entry = location.Entry;
            unitId = null;
        }

        var mode = kind == VocabQuestionKind.Review && cardState == MemoryCardState.Review
            ? VocabGenerationMode.Review : VocabGenerationMode.New;
        var family = _notebookSource is null ? null : await _notebookSource.ReadVerifiedFamilyAsync(studentId,
            new CardKey(entry.NormalizedWord, entry.MeaningKey), cancellationToken).ConfigureAwait(false);
        var (spec, generated) = await TryModeAsync(mode, unitId).ConfigureAwait(false);
        if (generated is null && kind == VocabQuestionKind.Review && mode == VocabGenerationMode.New)
        {
            // No choice question (for example no distractors): keep the card reviewable in review mode.
            (spec, generated) = await TryModeAsync(VocabGenerationMode.Review, spec.UnitId).ConfigureAwait(false);
        }

        if (generated is null)
        {

            _logger?.LogWarning(
                "Vocabulary question generation skipped for word {WordId} meaning {MeaningId} (kind {Kind}, plan {PlanId}).",
                entry.WordId, entry.MeaningId, kind, planId);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        // The family hint shown on a new question belongs to the card, not to one catalog location: a notebook
        // meaning learned at the plan's location keeps its hint. Review questions never carry the hint.
        var source = kind != VocabQuestionKind.Review ? family?.Source : null;
        if (bookId != spec.BookId || entry.WordId != spec.WordId || entry.MeaningId != spec.MeaningId)
        {
            bookId = spec.BookId;
            entry = entry with { WordId = spec.WordId, MeaningId = spec.MeaningId };
            unitId = spec.UnitId;
        }
        return BuildIssuedQuestion(studentId, planId, bookId, kind, entry, unitId, spec, generated, source, now);

        async Task<(VocabQuestionSpec Request, VocabGeneratedQuestion? Question)> TryModeAsync(
            VocabGenerationMode attempt, string? attemptUnitId)
        {
            var request = new VocabQuestionSpec(entry.WordId, bookId, entry.MeaningId, attemptUnitId, attempt);
            if (kind == VocabQuestionKind.Review && attempt == VocabGenerationMode.Review
                && family is { Source.Direction: "outgoing" } verified)
            {
                var relation = new VocabQuestionRelation(verified.Source.WordId, verified.Source.RelationType,
                    verified.Source.RelationId, verified.Source.MeaningId);
                if (IsTarget(verified.Location, request))
                {
                    request = request with { Relation = relation };
                }
                else
                {
                    var linkedRequest = request with
                    {
                        WordId = verified.Location.Entry.WordId,
                        BookId = verified.Location.BookId,
                        MeaningId = verified.Location.Entry.MeaningId,
                        UnitId = null,
                        Relation = relation
                    };
                    var linked = await _questionGenerator.CreateAsync(linkedRequest, cancellationToken).ConfigureAwait(false);
                    if (linked is not null) return (linkedRequest, linked);
                }
            }
            var question = await _questionGenerator.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            if (question is null && request.UnitId is not null)
            {
                request = request with { UnitId = null };
                question = await _questionGenerator.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            return (request, question);
        }
    }

    private static bool IsTarget(VocabularyPlanScopeEntry location, VocabQuestionSpec request) =>
        location.BookId == request.BookId && location.Entry.WordId == request.WordId && location.Entry.MeaningId == request.MeaningId;

    private VocabIssuedQuestion BuildIssuedQuestion(
        Guid studentId,
        Guid? planId,
        string bookId,
        VocabQuestionKind kind,
        VocabularyScopeEntry entry,
        string? unitId,
        VocabQuestionSpec spec,
        VocabGeneratedQuestion generated,
        VocabNotebookFamilySource? source,
        DateTimeOffset now)
    {
        var direction = generated.Direction;
        var fillLetters = direction.IsSpelling();
        // The adapter validates the provider contract; these guards keep an impossible shape out of storage.
        if (!Enum.IsDefined(direction)
            || (spec.Mode == VocabGenerationMode.New && direction != VocabQuestionDirection.EnglishToChinese)
            || (direction.IsLinked() && (kind != VocabQuestionKind.Review || spec.Relation is null))
            || (fillLetters && (generated.Options.Count != 0 || generated.Pattern is not { Count: > 0 } || string.IsNullOrEmpty(generated.AnswerWord))))
        {
            throw new VocabCatalogUnavailableException();
        }

        // Only choice questions retain options; fill-in-letters keeps the received answer and pattern.
        var options = fillLetters ? [] : generated.Options
            .Select(option => new VocabQuestionOption(Guid.NewGuid(), option.Text))
            .ToList();
        var correctIndex = -1;
        for (var index = 0; index < generated.Options.Count; index++)
        {
            if (generated.Options[index].IsCorrect)
            {
                correctIndex = index;
                break;
            }
        }
        if (!fillLetters && (correctIndex < 0 || options.Count < 2))
        {
            throw new VocabCatalogUnavailableException();
        }

        return new VocabIssuedQuestion
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            PlanId = planId,
            Kind = kind,
            WordKey = entry.NormalizedWord,
            MeaningKey = entry.MeaningKey,
            WordId = entry.WordId,
            MeaningId = entry.MeaningId,
            BookId = bookId,
            UnitId = unitId,
            Direction = direction,
            Prompt = generated.Prompt,
            AnchorWord = generated.AnchorWord,
            PartOfSpeech = generated.PartOfSpeech,
            RelationType = generated.RelationType,
            FamilySource = kind != VocabQuestionKind.Review && direction == VocabQuestionDirection.EnglishToChinese && source is not null
                ? new VocabFamilyHint(source.Word, source.RelationType, source.Direction) : null,
            Options = options,
            CorrectOptionId = fillLetters ? null : options[correctIndex].OptionId,
            AnswerWord = fillLetters ? generated.AnswerWord : null,
            LetterPattern = fillLetters ? generated.Pattern!.ToList() : null,
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(_options.QuestionExpiryMinutes),
            AnsweredAt = null,
        };
    }

    /// <summary>
    /// Free review scans all due cards in order, independently of daily assignments.
    /// The saved target is reused in review mode; Lexarbor chooses the question type.
    /// </summary>
    private async Task<VocabNextQuestionOutcome> ServeNextReviewAsync(
        Guid studentId, Guid? planId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        MemoryCardCursor? cursor = null;
        var sawDue = false;
        var paused = await ReadPausedCardKeysAsync(studentId, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dueCards = await _cardRepository.GetDuePageAsync(studentId, now, cursor, ReviewScanLimit, cancellationToken).ConfigureAwait(false);
            if (dueCards.Count == 0) return VocabNextQuestionOutcome.Done(sawDue ? "no-reviewable-due-cards" : "no-due-reviews");
            sawDue = true;
            foreach (var card in dueCards)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = new CardKey(card.WordKey, card.MeaningKey);
                if (paused.Contains(key)) continue;
                var question = await CreateReviewQuestionAsync(studentId, planId, key, card.State, now, cancellationToken).ConfigureAwait(false);
                if (question is null) continue;
                cancellationToken.ThrowIfCancellationRequested();
                await _questionRepository.AddRangeAsync([question], cancellationToken).ConfigureAwait(false);
                await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return VocabNextQuestionOutcome.Next(question);
            }
            var last = dueCards[^1];
            cursor = new MemoryCardCursor(last.DueAt, last.WordKey, last.MeaningKey);
        }
    }

    /// <summary>
    /// Reviews a card at the location saved when it was created (#443). Without one, or when it gives no
    /// question, the latest answered question's location stands in for this question only; the saved
    /// location never moves. Null when neither gives a question.
    /// </summary>
    private async Task<VocabIssuedQuestion?> CreateReviewQuestionAsync(Guid studentId, Guid? planId, CardKey key,
        MemoryCardState? state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var saved = await _cardLocationRepository.FindAsync(studentId, key, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            var question = await CreateQuestionAsync(studentId, planId, saved.BookId, VocabQuestionKind.Review,
                new(saved.WordId, key.WordKey, saved.MeaningId, key.MeaningKey, null), null, now, cancellationToken, state).ConfigureAwait(false);
            if (question is not null) return question;
        }
        var latest = await _questionRepository.FindLatestAnsweredByCardKeyAsync(studentId, key.WordKey, key.MeaningKey, cancellationToken).ConfigureAwait(false);
        if (latest is not null && !string.IsNullOrWhiteSpace(latest.BookId) && !string.IsNullOrWhiteSpace(latest.WordId)
            && !string.IsNullOrWhiteSpace(latest.MeaningId)
            && (saved is null || (saved.BookId, saved.WordId, saved.MeaningId) != (latest.BookId, latest.WordId, latest.MeaningId)))
        {
            var question = await CreateQuestionAsync(studentId, planId, latest.BookId, VocabQuestionKind.Review,
                new(latest.WordId, key.WordKey, latest.MeaningId, key.MeaningKey, null), null, now, cancellationToken, state).ConfigureAwait(false);
            if (question is not null) return question;
        }
        _logger?.LogInformation(
            "Vocabulary review skipped (plan {PlanId}): saved location {HasSaved}, answered location {HasAnswered}, no question.",
            planId, saved is not null, latest is not null);
        return null;
    }

    private async Task<VocabAnswerResult> LoadFirstResultAsync(VocabIssuedQuestion question, CancellationToken cancellationToken)
    {
        var first = await _attemptRepository.FindByQuestionIdAsync(question.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new VocabQuestionNotFoundException(question.Id);
        var card = await _cardRepository.FindAsync(question.StudentId, question.WordKey, question.MeaningKey, cancellationToken).ConfigureAwait(false);
        return new VocabAnswerResult(question.Id, first.Result, question.CorrectOptionId, first.Rating, card?.DueAt ?? first.AnsweredAt,
            question.Direction.IsSpelling() ? question.AnswerWord : null);
    }
}
