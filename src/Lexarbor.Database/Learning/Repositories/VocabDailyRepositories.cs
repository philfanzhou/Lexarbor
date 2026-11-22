using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Learning.Database.Entities;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Learning.Database.Repositories;

public class VocabPlanDayRepository : IVocabPlanDayRepository
{
    private static readonly JsonSerializerOptions KeysJsonOptions = new(JsonSerializerDefaults.Web);

    // Daily tasks have always been stored with the default options (PascalCase names); the
    // extra-batch lookup builds its patterns with the same options.
    private static readonly JsonSerializerOptions TasksJsonOptions = new();

    private readonly LearningDbContext _dbContext;

    public VocabPlanDayRepository(LearningDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<VocabPlanDayModel?> FindAsync(Guid planId, DateOnly localDate, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabPlanDays.FindAsync(
            new object[] { planId, localDate }, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<VocabPlanDayModel?> FindExtraBatchDayAsync(Guid planId, Guid? batchOrQuestionId = null, CancellationToken cancellationToken = default, bool review = false)
    {
        // jsonb containment compares structure, so spacing and key order in the stored text do not matter.
        var collection = review ? nameof(VocabDailyTasks.ExtraReviewBatches) : nameof(VocabDailyTasks.ExtraBatches);
        string[] patterns = batchOrQuestionId is { } id
            ? [ExtraBatchPattern(collection, nameof(VocabExtraBatch.Id), JsonValue.Create(id)),
                ExtraBatchPattern(collection, nameof(VocabExtraBatch.RequestIds), new JsonArray(JsonValue.Create(id))),
                ExtraBatchPattern(collection, nameof(VocabExtraBatch.QuestionIds), new JsonArray(JsonValue.Create(id)))]
            : [ExtraBatchPattern(collection, nameof(VocabExtraBatch.CompletedAt), null)];
        var candidates = _dbContext.VocabPlanDays.FromSql($"""
                SELECT * FROM vocab_plan_day
                WHERE plan_id = {planId} AND daily_tasks IS NOT NULL
                    AND daily_tasks::jsonb @> ANY (CAST({patterns} AS jsonb[]))
                """)
            .OrderByDescending(day => day.LocalDate).AsAsyncEnumerable();
        await foreach (var entity in candidates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var day = ToModel(entity);
            if (review)
            {
                if (day.Tasks!.ExtraReviewBatches.Any(batch => batchOrQuestionId is { } key
                    ? batch.Id == key || batch.RequestIds.Contains(key) || batch.QuestionIds.Contains(key) : batch.CompletedAt is null)) return day;
                continue;
            }
            if (day.Tasks!.ExtraBatches.Any(batch => batchOrQuestionId is { } id
                ? batch.Id == id || batch.RequestIds.Contains(id) || batch.QuestionIds.Contains(id) : batch.CompletedAt is null)) return day;
        }
        return null;
    }

    public async Task<VocabExtraBatchSnapshot?> FindLatestReviewBatchAsync(Guid planId, CancellationToken cancellationToken)
    {
        var anyBatch = new JsonObject { [nameof(VocabDailyTasks.ExtraReviewBatches)] = new JsonArray(new JsonObject()) }.ToJsonString();
        var openBatch = ExtraBatchPattern(nameof(VocabDailyTasks.ExtraReviewBatches), nameof(VocabReviewBatch.CompletedAt), null);
        var owner = await _dbContext.VocabPlanDays.FromSql($"""
            SELECT * FROM vocab_plan_day WHERE plan_id = {planId} AND daily_tasks::jsonb @> CAST({anyBatch} AS jsonb)
            ORDER BY (daily_tasks::jsonb @> CAST({openBatch} AS jsonb)) DESC, local_date DESC LIMIT 1
            """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (owner.Count == 0) return null;
        var batches = ToModel(owner[0]).Tasks!.ExtraReviewBatches;
        return (batches.LastOrDefault(b => b.CompletedAt is null) ?? batches[^1]).Snapshot();
    }

    private static string ExtraBatchPattern(string collection, string property, JsonNode? value) => new JsonObject
    {
        [JsonName(collection)] = new JsonArray(new JsonObject { [JsonName(property)] = value })
    }.ToJsonString();

    private static string JsonName(string property) => TasksJsonOptions.PropertyNamingPolicy?.ConvertName(property) ?? property;

    public Task AddAsync(VocabPlanDayModel day, CancellationToken cancellationToken = default)
    {
        return _dbContext.VocabPlanDays.AddAsync(ToEntity(day), cancellationToken).AsTask();
    }

    public async Task UpdateAsync(VocabPlanDayModel day, CancellationToken cancellationToken = default)
    {


        var entity = await _dbContext.VocabPlanDays.FindAsync(
            new object[] { day.PlanId, day.LocalDate }, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            throw new InvalidOperationException(
                $"Vocabulary plan day ({day.PlanId}, {day.LocalDate}) is not loaded and cannot be updated.");
        }

        entity.DailyTasks = day.Tasks is null ? null : JsonSerializer.Serialize(day.Tasks, TasksJsonOptions);
        entity.NewWordsTarget = day.NewWordsTarget;
        entity.NewWordsLearned = day.NewWordsLearned;
        entity.DueInScope = SerializeKeys(day.DueInScope);
        entity.ReviewedInScope = SerializeKeys(day.ReviewedInScope);
        entity.CompletedAt = day.CompletedAt;
        entity.NewWordsExhausted = day.NewWordsExhausted;
        entity.UpdatedAt = day.UpdatedAt;
        entity.Version = day.Version + 1;
        _dbContext.Entry(entity).Property(e => e.Version).OriginalValue = day.Version;
        day.Version = entity.Version;
    }

    public async Task ClearNewWordsExhaustedAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        // Tracked rows and stored rows both change, so a later save in this context keeps the cleared flag.

        var days = await _dbContext.VocabPlanDays
            .Where(d => d.PlanId == planId && d.NewWordsExhausted)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var day in days)
        {
            day.NewWordsExhausted = false;
            day.Version += 1;
        }
    }

    private static VocabPlanDayEntity ToEntity(VocabPlanDayModel model) => new()
    {
        PlanId = model.PlanId,
        LocalDate = model.LocalDate,
        DailyTasks = model.Tasks is null ? null : JsonSerializer.Serialize(model.Tasks, TasksJsonOptions),
        NewWordsTarget = model.NewWordsTarget,
        NewWordsLearned = model.NewWordsLearned,
        DueInScope = SerializeKeys(model.DueInScope),
        ReviewedInScope = SerializeKeys(model.ReviewedInScope),
        CompletedAt = model.CompletedAt,
        NewWordsExhausted = model.NewWordsExhausted,
        Version = model.Version,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
    };

    private static VocabPlanDayModel ToModel(VocabPlanDayEntity entity) => new()
    {
        PlanId = entity.PlanId,
        LocalDate = entity.LocalDate,
        Tasks = entity.DailyTasks is null ? null : JsonSerializer.Deserialize<VocabDailyTasks>(entity.DailyTasks, TasksJsonOptions),
        NewWordsTarget = entity.NewWordsTarget,
        NewWordsLearned = entity.NewWordsLearned,
        DueInScope = DeserializeKeys(entity.DueInScope),
        ReviewedInScope = DeserializeKeys(entity.ReviewedInScope),
        CompletedAt = entity.CompletedAt,
        NewWordsExhausted = entity.NewWordsExhausted,
        Version = entity.Version,
        CreatedAt = entity.CreatedAt,
        UpdatedAt = entity.UpdatedAt,
    };

    private static string SerializeKeys(List<CardKey> keys) =>
        JsonSerializer.Serialize(keys.Select(key => new CardKeyJson(key.WordKey, key.MeaningKey)), KeysJsonOptions);

    private static List<CardKey> DeserializeKeys(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }
        var parsed = JsonSerializer.Deserialize<List<CardKeyJson>>(json, KeysJsonOptions) ?? [];
        return parsed.Select(key => new CardKey(key.W, key.M)).ToList();
    }

    private sealed record CardKeyJson(string W, string M);
}

public class VocabIssuedQuestionRepository : IVocabIssuedQuestionRepository
{
    private static readonly JsonSerializerOptions OptionsJson = new(JsonSerializerDefaults.Web);

    private readonly LearningDbContext _dbContext;

    public VocabIssuedQuestionRepository(LearningDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<VocabIssuedQuestionModel?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabIssuedQuestions.FindAsync(new object[] { id }, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public async Task LockNewWordAnswersAsync(Guid studentId, string wordKey, CancellationToken cancellationToken = default)
    {
        if (!_dbContext.Database.IsRelational()) return;
        var key = $"Student/vocab/new-word-answer/{studentId:N}/{wordKey}";
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkAnsweredAsync(Guid id, DateTimeOffset answeredAt, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabIssuedQuestions.FindAsync(new object[] { id }, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            throw new InvalidOperationException($"Vocabulary question {id} is not loaded and cannot be marked answered.");
        }
        entity.AnsweredAt = answeredAt;
    }

    public async Task<VocabIssuedQuestionModel?> FindPendingByKindAsync(Guid studentId, VocabQuestionKind kind, bool freeReview, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabIssuedQuestions.AsNoTracking()
            .Where(question => question.StudentId == studentId
                && question.Kind == (int)kind
                && (freeReview ? question.PlanId == null : question.PlanId != null)
                && question.AnsweredAt == null
                && question.ExpiresAt > now)
            .OrderBy(question => question.IssuedAt)
            .ThenBy(question => question.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<HashSet<string>> FindPendingNewWordKeysAsync(Guid studentId, IReadOnlyCollection<string> wordKeys, DateTimeOffset now, Guid? exceptPlanId = null, CancellationToken cancellationToken = default)
    {
        if (wordKeys.Count == 0)
        {
            return [];
        }

        var entities = await _dbContext.VocabIssuedQuestions.AsNoTracking()
            .Where(question => question.StudentId == studentId
                && (question.Kind == (int)VocabQuestionKind.New || question.Kind == (int)VocabQuestionKind.Extra)
                && question.AnsweredAt == null
                && question.ExpiresAt > now
                && wordKeys.Contains(question.WordKey)
                && (exceptPlanId == null || question.PlanId != exceptPlanId))
            .Select(question => question.WordKey)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<HashSet<CardKey>> FindPendingReviewCardKeysAsync(Guid studentId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.VocabIssuedQuestions.AsNoTracking()
            .Where(question => question.StudentId == studentId
                && question.Kind == (int)VocabQuestionKind.Review
                && question.AnsweredAt == null
                && question.ExpiresAt > now)
            .Select(question => new { question.WordKey, question.MeaningKey })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(key => new CardKey(key.WordKey, key.MeaningKey)).ToHashSet();
    }

    public async Task<VocabIssuedQuestionModel?> FindLatestAnsweredByCardKeyAsync(Guid studentId, string wordKey, string meaningKey, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabIssuedQuestions.AsNoTracking()
            .Where(question => question.StudentId == studentId
                && question.WordKey == wordKey
                && question.MeaningKey == meaningKey
                && question.AnsweredAt != null)
            .OrderByDescending(question => question.IssuedAt)
            .ThenByDescending(question => question.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public Task<bool> HasPendingForWordAsync(Guid studentId, string wordKey, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        return _dbContext.VocabIssuedQuestions.AsNoTracking()
            .AnyAsync(question => question.StudentId == studentId
                && question.WordKey == wordKey
                && (question.Kind == (int)VocabQuestionKind.New || question.Kind == (int)VocabQuestionKind.Extra)
                && question.AnsweredAt == null
                && question.ExpiresAt > now, cancellationToken);
    }

    public async Task<VocabIssuedQuestionModel?> FindPendingDailyAsync(Guid planId, VocabQuestionKind kind,
        IReadOnlyCollection<CardKey> keys, DateTimeOffset now, CancellationToken cancellationToken = default, IReadOnlySet<Guid>? excluded = null)
    {
        if (keys.Count == 0) return null;
        var pending = await _dbContext.VocabIssuedQuestions.AsNoTracking()
            .Where(question => question.PlanId == planId && question.Kind == (int)kind
                && question.AnsweredAt == null && question.ExpiresAt > now)
            .OrderBy(question => question.IssuedAt).ThenBy(question => question.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var match = pending.FirstOrDefault(question => excluded?.Contains(question.Id) != true && keys.Contains(new CardKey(question.WordKey, question.MeaningKey)));
        return match is null ? null : ToModel(match);
    }

    public Task AddRangeAsync(IReadOnlyCollection<VocabIssuedQuestionModel> questions, CancellationToken cancellationToken = default)
    {
        if (questions.Count == 0)
        {
            return Task.CompletedTask;
        }
        return _dbContext.VocabIssuedQuestions.AddRangeAsync(
            questions.Select(ToEntity), cancellationToken);
    }

    private static VocabIssuedQuestionEntity ToEntity(VocabIssuedQuestionModel model) => new()
    {
        Id = model.Id,
        StudentId = model.StudentId,
        PlanId = model.PlanId,
        Kind = (int)model.Kind,
        WordKey = model.WordKey,
        MeaningKey = model.MeaningKey,
        WordId = model.WordId,
        MeaningId = model.MeaningId,
        BookId = model.BookId,
        UnitId = model.UnitId,
        Direction = (int)model.Direction,
        Prompt = model.Prompt,
        AnchorWord = model.AnchorWord,
        PartOfSpeech = model.PartOfSpeech,
        RelationType = model.RelationType,
        FamilySourceWord = model.FamilySource?.Word,
        FamilySourceRelationType = model.FamilySource?.RelationType,
        FamilySourceDirection = model.FamilySource?.Direction,
        Source = (short?)model.Source,
        OptionsSnapshot = model.Direction.IsSpelling() ? null : JsonSerializer.Serialize(
            model.Options.Select(option => new OptionJson(option.OptionId.ToString("N"), option.Text)), OptionsJson),
        CorrectOptionId = model.CorrectOptionId,
        LetterPattern = model.LetterPattern is null ? null : JsonSerializer.Serialize(model.LetterPattern),
        AnswerWord = model.AnswerWord,
        IssuedAt = model.IssuedAt,
        ExpiresAt = model.ExpiresAt,
        AnsweredAt = model.AnsweredAt,
    };

    private static VocabIssuedQuestionModel ToModel(VocabIssuedQuestionEntity entity) => new()
    {
        Id = entity.Id,
        StudentId = entity.StudentId,
        PlanId = entity.PlanId,
        Kind = (VocabQuestionKind)entity.Kind,
        WordKey = entity.WordKey,
        MeaningKey = entity.MeaningKey,
        WordId = entity.WordId,
        MeaningId = entity.MeaningId,
        BookId = entity.BookId,
        UnitId = entity.UnitId,
        Direction = (VocabQuestionDirection)entity.Direction,
        Prompt = entity.Prompt,
        AnchorWord = entity.AnchorWord,
        PartOfSpeech = entity.PartOfSpeech,
        RelationType = entity.RelationType,
        FamilySource = entity.FamilySourceWord is { } word && entity.FamilySourceRelationType is { } relation
            && entity.FamilySourceDirection is { } direction ? new VocabFamilyHint(word, relation, direction) : null,
        Source = (VocabularySourceKind?)entity.Source,
        Options = DeserializeOptions(entity.OptionsSnapshot),
        CorrectOptionId = entity.CorrectOptionId,
        LetterPattern = entity.LetterPattern is null ? null : JsonSerializer.Deserialize<List<string?>>(entity.LetterPattern),
        AnswerWord = entity.AnswerWord,
        IssuedAt = entity.IssuedAt,
        ExpiresAt = entity.ExpiresAt,
        AnsweredAt = entity.AnsweredAt,
    };

    private static List<VocabQuestionOption> DeserializeOptions(string? json)
    {
        var parsed = string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<List<OptionJson>>(json, OptionsJson) ?? [];
        return parsed.Select(option => new VocabQuestionOption(Guid.Parse(option.OptionId), option.Text)).ToList();
    }

    private sealed record OptionJson(string OptionId, string Text);
}

public class VocabAttemptRepository : IVocabAttemptRepository
{
    private readonly LearningDbContext _dbContext;

    public VocabAttemptRepository(LearningDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<VocabAttemptModel?> FindByQuestionIdAsync(Guid questionId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabAttempts.FindAsync(new object[] { questionId }, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public Task AddAsync(VocabAttemptModel attempt, CancellationToken cancellationToken = default)
    {
        return _dbContext.VocabAttempts.AddAsync(new VocabAttemptEntity
        {
            IssuedQuestionId = attempt.IssuedQuestionId,
            Result = (int)attempt.Result,
            SelectedOptionId = attempt.SelectedOptionId,
            FilledLetters = attempt.FilledLetters is null ? null : JsonSerializer.Serialize(attempt.FilledLetters),
            ElapsedMs = attempt.ElapsedMs,
            Rating = (int)attempt.Rating,
            AnsweredAt = attempt.AnsweredAt,
            LocalDate = attempt.LocalDate,
        }, cancellationToken).AsTask();
    }

    private static VocabAttemptModel ToModel(VocabAttemptEntity entity) => new()
    {
        IssuedQuestionId = entity.IssuedQuestionId,
        Result = (VocabAttemptResult)entity.Result,
        SelectedOptionId = entity.SelectedOptionId,
        FilledLetters = entity.FilledLetters is null ? null : JsonSerializer.Deserialize<List<string>>(entity.FilledLetters),
        ElapsedMs = entity.ElapsedMs,
        Rating = (ReviewRating)entity.Rating,
        AnsweredAt = entity.AnsweredAt,
        LocalDate = entity.LocalDate,
    };
}

public sealed class VocabCardLocationRepository(LearningDbContext dbContext) : IVocabCardLocationRepository
{
    public async Task<VocabCardLocation?> FindAsync(Guid studentId, CardKey key, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.VocabCardLocations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.StudentId == studentId && row.WordKey == key.WordKey && row.MeaningKey == key.MeaningKey,
                cancellationToken).ConfigureAwait(false);
        return entity is null ? null : new VocabCardLocation(entity.StudentId, new CardKey(entity.WordKey, entity.MeaningKey),
            entity.BookId, entity.WordId, entity.MeaningId, (VocabularySourceKind?)entity.Source, entity.SourceQuestionId, entity.RecordedAt);
    }

    public Task AddAsync(VocabCardLocation location, CancellationToken cancellationToken = default) =>
        dbContext.VocabCardLocations.AddAsync(new VocabCardLocationEntity
        {
            StudentId = location.StudentId,
            WordKey = location.Key.WordKey,
            MeaningKey = location.Key.MeaningKey,
            BookId = location.BookId,
            WordId = location.WordId,
            MeaningId = location.MeaningId,
            Source = (short?)location.Source,
            SourceQuestionId = location.SourceQuestionId,
            RecordedAt = location.RecordedAt,
        }, cancellationToken).AsTask();
}
