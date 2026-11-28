using System.Data;
using Lexarbor.Learning.Database.Entities;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Lexarbor.Learning.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lexarbor.Learning.Database.Repositories;

public sealed class VocabNotebookRepository : IVocabNotebookRepository
{
    private readonly LearningDbContext _dbContext;

    public VocabNotebookRepository(LearningDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<VocabNotebookItem>> ListAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.VocabNotebookItems.AsNoTracking().Include(item => item.FamilySources)
            .Where(item => item.StudentId == studentId)
            .OrderBy(item => item.AddedAt).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToModel).ToList();
    }

    private IQueryable<VocabNotebookItemEntity> Items(Guid studentId) =>
        _dbContext.VocabNotebookItems.AsNoTracking().Where(item => item.StudentId == studentId);

    private IQueryable<VocabNotebookItemEntity> Pending(Guid studentId) =>
        Items(studentId).Where(item => item.Status == (short)VocabNotebookItemStatus.Pending && item.SubmissionId != null);

    public Task<List<string>> PendingSubmissionIdsAsync(Guid studentId, CancellationToken cancellationToken) =>
        Pending(studentId).Select(item => item.SubmissionId!).Distinct().ToListAsync(cancellationToken);

    public async Task<List<VocabNotebookItem>> ReviewedPendingAsync(Guid studentId, string[] submissionIds,
        CancellationToken cancellationToken)
    {
        var rows = await Pending(studentId).Where(item => submissionIds.Contains(item.SubmissionId!))
            .OrderBy(item => item.AddedAt).ThenBy(item => item.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToModel).ToList();
    }

    public async Task<VocabNotebookSummary> SummaryAsync(Guid studentId, DateTimeOffset recentStart,
        CancellationToken cancellationToken) =>
        await Items(studentId).GroupBy(item => item.StudentId).Select(group => new VocabNotebookSummary(
            group.Select(item => item.WordKey).Distinct().Count(), group.Count(),
            group.Count(item => item.AddedBy == (short)VocabNotebookActorType.Teacher),
            group.Count(item => item.AddedAt >= recentStart),
            group.Count(item => item.Status == (short)VocabNotebookItemStatus.Rejected)))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? new(0, 0, 0, 0, 0);

    public async Task<VocabNotebookPage<VocabNotebookItem>> QueryAsync(Guid studentId, VocabNotebookQuery filters,
        DateTimeOffset recentStart, CancellationToken cancellationToken)
    {
        if (filters.Keyword.Contains('\0')) return new(0, 1, []);
        var query = Items(studentId);
        if (filters.Teacher) query = query.Where(item => item.AddedBy == (short)VocabNotebookActorType.Teacher);
        if (filters.Recent) query = query.Where(item => item.AddedAt >= recentStart);
        if (filters.Rejected) query = query.Where(item => item.Status == (short)VocabNotebookItemStatus.Rejected);
        if (filters.Keyword.Length > 0)
        {
            // Escape the LIKE metacharacters so every search term is a literal substring.
            var pattern = "%" + filters.Keyword.Replace("^", "^^").Replace("%", "^%").Replace("_", "^_") + "%";
            query = query.Where(item => EF.Functions.ILike(item.Word, pattern, "^") || EF.Functions.ILike(item.Meaning, pattern, "^"));
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        if (total == 0) return new(0, 1, []);
        var page = Math.Min(filters.Page, 1 + (total - 1) / VocabNotebookQuery.PageSize);
        var rows = await query.OrderByDescending(item => item.AddedAt).ThenBy(item => item.Id)
            .Skip((page - 1) * VocabNotebookQuery.PageSize).Take(VocabNotebookQuery.PageSize)
            .Include(item => item.FamilySources).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new(total, page, rows.Select(ToModel).ToList());
    }

    public Task<List<CardKey>> MembershipAsync(Guid studentId, IReadOnlyCollection<CardKey> keys, CancellationToken cancellationToken)
    {
        var words = keys.Select(key => key.WordKey).ToArray();
        var meanings = keys.Select(key => key.MeaningKey).ToArray();
        return _dbContext.Database.SqlQuery<CardKey>($"""
            SELECT item.word_key AS "WordKey", item.meaning_key AS "MeaningKey"
            FROM unnest({words}, {meanings}) AS candidate(word_key, meaning_key)
            JOIN vocab_notebook_item AS item USING (word_key, meaning_key)
            WHERE item.student_id = {studentId} AND item.status <> {(short)VocabNotebookItemStatus.Rejected}
            """).ToListAsync(cancellationToken);
    }

    public async Task<VocabNotebookItem?> FindAsync(Guid studentId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabNotebookItems.AsNoTracking().Include(item => item.FamilySources)
            .SingleOrDefaultAsync(item => item.StudentId == studentId && item.Id == itemId, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<VocabNotebookItem?> FindByKeyAsync(Guid studentId, CardKey key, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabNotebookItems.AsNoTracking().Include(item => item.FamilySources)
            .SingleOrDefaultAsync(item => item.StudentId == studentId && item.WordKey == key.WordKey && item.MeaningKey == key.MeaningKey,
                cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public Task AddAsync(VocabNotebookItem item, CancellationToken cancellationToken = default) =>
        _dbContext.VocabNotebookItems.AddAsync(Apply(item, new VocabNotebookItemEntity()), cancellationToken).AsTask();

    public async Task UpdateAsync(VocabNotebookItem item, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabNotebookItems.Include(row => row.FamilySources)
            .SingleOrDefaultAsync(row => row.Id == item.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Notebook item {item.Id} is not loaded and cannot be updated.");
        var existingSourceIds = entity.FamilySources.Select(source => source.Id).ToHashSet();
        Apply(item, entity);
        foreach (var source in entity.FamilySources.Where(source => !existingSourceIds.Contains(source.Id)))
            _dbContext.Entry(source).State = EntityState.Added;
    }

    public async Task RemoveAsync(VocabNotebookItem item, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.VocabNotebookItems.FindAsync([item.Id], cancellationToken).ConfigureAwait(false);
        if (entity is not null) _dbContext.VocabNotebookItems.Remove(entity);
    }

    public async Task<T> InStudentWriteAsync<T>(Guid studentId, Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        // ReadCommitted sees the previous writer after waiting for the student-scoped lock.
        var relational = _dbContext.Database.IsRelational();
        await using var transaction = relational && _dbContext.Database.CurrentTransaction is null
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            if (relational)
                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({studentId.ToString()}, 9132))", cancellationToken).ConfigureAwait(false);
            var result = await work().ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (IsWriteConflict(error))
        {
            _dbContext.ChangeTracker.Clear();
            throw new VocabNotebookWriteConflictException(error);
        }
    }

    public async Task<T> InRepeatableReadAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        // The in-memory provider used by the service tests has no transactions; PostgreSQL always opens one.
        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false)
            : null;
        T result;
        try
        {
            result = await work().ConfigureAwait(false);
        }
        catch (Exception error) when (IsWriteConflict(error))
        {
            _dbContext.ChangeTracker.Clear();
            throw new VocabNotebookWriteConflictException(error);
        }

        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static bool IsWriteConflict(Exception error)
    {
        // Npgsql's execution strategy wraps a transient failure, such as a serialization failure, in InvalidOperationException.
        var update = error as DbUpdateException ?? (error as InvalidOperationException)?.InnerException as DbUpdateException;
        return update is DbUpdateConcurrencyException
            || update?.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure };
    }

    private static VocabNotebookItem ToModel(VocabNotebookItemEntity entity)
    {
        var item = new VocabNotebookItem
        {
            Id = entity.Id,
            StudentId = entity.StudentId,
            WordKey = entity.WordKey,
            MeaningKey = entity.MeaningKey,
            Word = entity.Word,
            PartOfSpeech = entity.PartOfSpeech,
            Meaning = entity.Meaning,
            Status = (VocabNotebookItemStatus)entity.Status,
            BookId = entity.BookId,
            WordId = entity.WordId,
            MeaningId = entity.MeaningId,
            SubmissionId = entity.SubmissionId,
            ReviewComment = entity.ReviewComment,
            Original = entity.OriginalWord is null || entity.OriginalMeaning is null
                ? null
                : new VocabNotebookOriginal(entity.OriginalWord, entity.OriginalPartOfSpeech, entity.OriginalMeaning),
            FamilyOnly = entity.FamilyOnly,
            AddedBy = (VocabNotebookActorType)entity.AddedBy,
            AddedByTeacherId = entity.AddedByTeacherId,
            AddedAt = entity.AddedAt,
            UpdatedAt = entity.UpdatedAt,
        };
        item.FamilySources.AddRange(entity.FamilySources.OrderBy(source => source.RelationType, StringComparer.Ordinal)
            .ThenBy(source => source.Id).Select(SourceModel));
        return item;
    }

    private static VocabNotebookFamilySource SourceModel(VocabNotebookFamilySourceEntity source) =>
        new(source.SourceWordKey, source.SourceWord, source.SourceWordId, source.RelationType, source.Direction,
            source.RelationId, source.SourceMeaningId.Length == 0 ? null : source.SourceMeaningId,
            source.SourceBookId.Length == 0 ? null : source.SourceBookId,
            new(source.TargetWordId, source.TargetBookId, source.TargetMeaningId));

    private static VocabNotebookItemEntity Apply(VocabNotebookItem item, VocabNotebookItemEntity entity)
    {
        entity.Id = item.Id;
        entity.StudentId = item.StudentId;
        entity.WordKey = item.WordKey;
        entity.MeaningKey = item.MeaningKey;
        entity.Word = item.Word;
        entity.PartOfSpeech = item.PartOfSpeech;
        entity.Meaning = item.Meaning;
        entity.Status = (short)item.Status;
        entity.BookId = item.BookId;
        entity.WordId = item.WordId;
        entity.MeaningId = item.MeaningId;
        entity.SubmissionId = item.SubmissionId;
        entity.ReviewComment = item.ReviewComment;
        entity.OriginalWord = item.Original?.Word;
        entity.OriginalPartOfSpeech = item.Original?.PartOfSpeech;
        entity.OriginalMeaning = item.Original?.Meaning;
        foreach (var original in item.FamilySources)
        {
            var source = original with { Target = original.Target ?? new(item.WordId!, item.BookId!, item.MeaningId!) };
            if (entity.FamilySources.Any(prior => SourceModel(prior).Covers(source))) continue;
            var sameIdentity = entity.FamilySources.FirstOrDefault(prior => SourceModel(prior).SameIdentity(source));
            if (sameIdentity is not null)
            {
                // A provider may recreate the same catalog edge with a new relation ID.
                sameIdentity.RelationId = source.RelationId;
                continue;
            }
            // A verified pair upgrades the corresponding word-level annotation without discarding other pairs.
            entity.FamilySources.RemoveAll(prior => !SourceModel(prior).HasMeaningPair && SourceModel(prior).SameRelation(source));
            entity.FamilySources.Add(new()
            {
                Id = Guid.NewGuid(),
                NotebookItemId = item.Id,
                SourceWordKey = source.WordKey,
                SourceWord = source.Word,
                SourceWordId = source.WordId,
                RelationType = source.RelationType,
                Direction = source.Direction,
                RelationId = source.RelationId,
                SourceMeaningId = source.MeaningId ?? string.Empty,
                SourceBookId = source.BookId ?? string.Empty,
                TargetWordId = source.Target!.WordId,
                TargetBookId = source.Target.BookId,
                TargetMeaningId = source.Target.MeaningId,
            });
        }
        entity.FamilyOnly = item.FamilyOnly;
        entity.AddedBy = (short)item.AddedBy;
        entity.AddedByTeacherId = item.AddedByTeacherId;
        entity.AddedAt = item.AddedAt;
        entity.UpdatedAt = item.UpdatedAt;
        return entity;
    }
}
