using System.Text.Json;
using Lexarbor.Learning.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lexarbor.Learning.Database.Repositories;

public sealed record PhotoTaskEntry(VocabPhotoCandidate Candidate, Guid? NotebookItemId = null);
public sealed record PhotoTaskSummary(Guid Id, string State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int Attempts, string? ErrorCode, int EntryCount, int AddedCount);
public sealed record PhotoTaskDetail(string State, string Entries, Guid Id, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int Attempts, string? ErrorCode, int AddedCount)
{
    public List<PhotoTaskEntry> ReadEntries() => JsonSerializer.Deserialize<List<PhotoTaskEntry>>(Entries)!;
}
public sealed record PhotoTaskClaim(Guid Id, Guid StudentId, int Attempts, byte[] Photo);

public sealed class PhotoImportTaskRepository(LearningDbContext db)
{
    public async Task<Guid?> CreateAsync(Guid studentId, byte[] photo, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_photo_task(id, student_id, photo)
            SELECT {id}, id, {photo} FROM learner_identity WHERE id = {studentId}
            """, ct);
        return inserted == 1 ? id : null;
    }

    public Task<List<PhotoTaskSummary>> ListAsync(Guid studentId, int page, CancellationToken ct) =>
        db.Database.SqlQuery<PhotoTaskSummary>($"""
            SELECT id AS "Id", state AS "State", created_at AS "CreatedAt", updated_at AS "UpdatedAt",
                   attempts AS "Attempts", error_code AS "ErrorCode", jsonb_array_length(entries) AS "EntryCount",
                   added_count AS "AddedCount"
            FROM vocab_photo_task WHERE student_id = {studentId}
            ORDER BY created_at DESC, id LIMIT 50 OFFSET {((long)page - 1) * 50}
            """).ToListAsync(ct);

    public async Task<PhotoTaskDetail?> ReadAsync(Guid studentId, Guid id, bool forUpdate, CancellationToken ct)
    {
        if (forUpdate)
            await db.Database.ExecuteSqlAsync($"SELECT id FROM vocab_photo_task WHERE student_id={studentId} AND id={id} FOR UPDATE", ct);
        return await db.Database.SqlQuery<PhotoTaskDetail>($"""
            SELECT id AS "Id", state AS "State", entries::text AS "Entries", created_at AS "CreatedAt",
                   updated_at AS "UpdatedAt", attempts AS "Attempts", error_code AS "ErrorCode", added_count AS "AddedCount"
            FROM vocab_photo_task WHERE student_id={studentId} AND id={id}
            """).SingleOrDefaultAsync(ct);
    }

    public Task<int> RetryAsync(Guid studentId, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE vocab_photo_task SET state='queued', updated_at=now(), error_code=NULL
            WHERE student_id={studentId} AND id={id} AND state='failed'
            """, ct);

    public Task<int> DeleteAsync(Guid studentId, Guid id, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"DELETE FROM vocab_photo_task WHERE student_id={studentId} AND id={id}", ct);

    // The caller holds the task row lock in the same transaction as the notebook write.
    public Task<int> SaveProgressAsync(Guid studentId, Guid id, List<PhotoTaskEntry> entries, int added, CancellationToken ct) =>
        added == entries.Count ? DeleteAsync(studentId, id, ct) : db.Database.ExecuteSqlAsync($"""
            UPDATE vocab_photo_task SET entries={JsonSerializer.Serialize(entries)}::jsonb, added_count={added}, updated_at=now()
            WHERE student_id={studentId} AND id={id}
            """, ct);

    public async Task<PhotoTaskClaim?> ClaimAsync(CancellationToken ct)
    {
        // A host may disappear after committing a claim. The attempt number fences out a late result.
        var rows = await db.Database.SqlQuery<PhotoTaskClaim>($"""
            WITH next AS (
                SELECT id FROM vocab_photo_task WHERE state='queued'
                    OR (state='processing' AND updated_at < now() - interval '10 minutes')
                ORDER BY created_at, id LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            UPDATE vocab_photo_task t SET state='processing', attempts=attempts+1, updated_at=now()
            FROM next WHERE t.id=next.id
            RETURNING t.id AS "Id", student_id AS "StudentId", attempts AS "Attempts", photo AS "Photo"
            """).ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    public async Task<int> FinishAsync(PhotoTaskClaim claim, IReadOnlyList<VocabPhotoCandidate> candidates, string? error, CancellationToken ct)
    {
        var entries = JsonSerializer.Serialize(candidates.Select(candidate => new PhotoTaskEntry(candidate)));
        try
        {
            return await SaveResultAsync(claim, entries, error, ct);
        }
        catch (PostgresException refused) when (error is null && refused.SqlState == PostgresErrorCodes.UntranslatableCharacter)
        {
            // jsonb cannot represent a U+0000 in otherwise valid model output. Keep the photo for an explicit retry.
            return await SaveResultAsync(claim, "[]", "VOCAB_PHOTO_IMPORT_UNAVAILABLE", ct);
        }
    }

    private Task<int> SaveResultAsync(PhotoTaskClaim claim, string entries, string? error, CancellationToken ct)
    {
        return db.Database.ExecuteSqlAsync($"""
            UPDATE vocab_photo_task SET state={(error is null ? "ready" : "failed")}, error_code={error},
                entries={entries}::jsonb, photo=CASE WHEN {error == null} THEN NULL ELSE photo END, updated_at=now()
            WHERE id={claim.Id} AND student_id={claim.StudentId} AND state='processing' AND attempts={claim.Attempts}
            """, ct);
    }
}
