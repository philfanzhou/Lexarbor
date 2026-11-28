using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

public interface IVocabPlanDayRepository
{
    Task<VocabPlanDay?> FindAsync(Guid planId, DateOnly localDate, CancellationToken cancellationToken = default);

    Task AddAsync(VocabPlanDay day, CancellationToken cancellationToken = default);

    Task<VocabPlanDay?> FindExtraBatchDayAsync(Guid planId, Guid? batchOrQuestionId = null, CancellationToken cancellationToken = default);
    Task<VocabPlanDay?> FindReviewBatchDayAsync(Guid planId, Guid? batchId, CancellationToken cancellationToken);
    Task<VocabPlanDay?> FindReviewQuestionDayAsync(Guid planId, Guid questionId, CancellationToken cancellationToken);
    Task<VocabExtraBatchSnapshot?> FindLatestReviewBatchAsync(Guid planId, CancellationToken cancellationToken);
    Task UpdateAsync(VocabPlanDay day, CancellationToken cancellationToken = default);
    Task ClearNewWordsExhaustedAsync(Guid planId, CancellationToken cancellationToken = default);
}

public interface IVocabIssuedQuestionRepository
{
    Task<VocabIssuedQuestion?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Serializes settlement of a student's new-word meanings within the current transaction.</summary>
    Task LockNewWordAnswersAsync(Guid studentId, string wordKey, CancellationToken cancellationToken = default);

    Task MarkAnsweredAsync(Guid id, DateTimeOffset answeredAt, CancellationToken cancellationToken = default);

    /// <summary>Next pending question of the requested kind, ordered by issuance. Free and plan-associated queues remain separate.</summary>
    Task<VocabIssuedQuestion?> FindPendingByKindAsync(Guid studentId, VocabQuestionKind kind, bool freeReview, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Pending new/extra word keys, optionally excluding the plan whose queue can be resumed.</summary>
    Task<HashSet<string>> FindPendingNewWordKeysAsync(Guid studentId, IReadOnlyCollection<string> wordKeys, DateTimeOffset now, Guid? exceptPlanId = null, CancellationToken cancellationToken = default);

    Task<HashSet<CardKey>> FindPendingReviewKeysAsync(Guid studentId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>The latest answered question of a card; unanswered and expired questions are ignored.</summary>
    Task<VocabIssuedQuestion?> FindLatestAnsweredAsync(Guid studentId, string wordKey, string meaningKey, CancellationToken cancellationToken = default);

    Task<bool> HasPendingForWordAsync(Guid studentId, string wordKey, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<VocabIssuedQuestion?> FindPendingDailyAsync(Guid planId, VocabQuestionKind kind,
        IReadOnlyCollection<CardKey> keys, DateTimeOffset now, CancellationToken cancellationToken = default, IReadOnlySet<Guid>? excluded = null);

    Task AddRangeAsync(IReadOnlyCollection<VocabIssuedQuestion> questions, CancellationToken cancellationToken = default);
}

public interface IVocabAttemptRepository
{
    Task<VocabAttempt?> FindByQuestionIdAsync(Guid questionId, CancellationToken cancellationToken = default);

    Task AddAsync(VocabAttempt attempt, CancellationToken cancellationToken = default);
}

public interface IVocabCardLocationRepository
{
    Task<VocabCardLocation?> FindAsync(Guid studentId, CardKey key, CancellationToken cancellationToken = default);

    Task AddAsync(VocabCardLocation location, CancellationToken cancellationToken = default);
}
