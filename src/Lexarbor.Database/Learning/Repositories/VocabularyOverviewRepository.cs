using System.Data;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lexarbor.Learning.Database.Repositories;

public sealed class VocabularyOverviewRepository(LearningDbContext context) : IVocabularyOverviewRepository
{
    public async Task<IVocabularyOverviewSnapshot> OpenAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        await using var live = await OpenLiveAsync(studentId, cancellationToken).ConfigureAwait(false);
        var cards = live.StudentExists ? await live.ReadCardsAsync(cancellationToken).ConfigureAwait(false) : [];
        // Materialize the consistent database snapshot before the caller waits for catalog HTTP.
        return new MaterializedSnapshot(live.StudentExists, live.ActivePlan, cards);
    }

    private async Task<IVocabularyOverviewSnapshot> OpenLiveAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
            // Fixed projections use EF's relational command pipeline without per-request query compilation.
            await using var rows = await LearningCommands.ExecuteReaderAsync(context, """
                SELECT s.id, p.id, p.segments, p.daily_new_words, p.controller::integer,
                    p.controller_teacher_id, p.status::integer, p.start_date, p.total_words,
                    p.version, p.created_at, p.updated_at
                FROM learner_identity s
                LEFT JOIN vocab_plan p ON p.student_id = s.id AND p.status = 1
                WHERE s.id = {0}
                """, [studentId], cancellationToken).ConfigureAwait(false);
            var exists = await rows.ReadAsync(cancellationToken).ConfigureAwait(false);
            var reader = rows.DbDataReader;
            VocabPlan? plan = null;
            if (exists && !reader.IsDBNull(1))
            {
                plan = new VocabPlan
                {
                    StudentId = reader.GetGuid(0),
                    Id = reader.GetGuid(1),
                    Segments = VocabPlanRepository.DeserializeSegments(reader.GetString(2)),
                    DailyNewWords = reader.GetInt32(3),
                    Controller = (VocabPlanController)reader.GetInt32(4),
                    ControllerTeacherId = reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    Status = (VocabPlanStatus)reader.GetInt32(6),
                    StartDate = reader.GetFieldValue<DateOnly>(7),
                    TotalWords = reader.GetInt32(8),
                    Version = reader.GetInt32(9),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(10),
                    UpdatedAt = reader.GetFieldValue<DateTimeOffset>(11),
                };
            }
            return new Snapshot(context, transaction, studentId, exists, plan);
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class MaterializedSnapshot(bool exists, VocabPlan? plan, IReadOnlyList<MemoryCard> cards) : IVocabularyOverviewSnapshot
    {
        public bool StudentExists => exists;
        public VocabPlan? ActivePlan => plan;
        public Task<IReadOnlyList<MemoryCard>> ReadCardsAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(cards); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Snapshot(LearningDbContext context, IDbContextTransaction transaction,
        Guid studentId, bool exists, VocabPlan? plan) : IVocabularyOverviewSnapshot
    {
        public bool StudentExists => exists;
        public VocabPlan? ActivePlan => plan;

        public async Task<IReadOnlyList<MemoryCard>> ReadCardsAsync(CancellationToken cancellationToken = default)
        {
            await using var rows = await LearningCommands.ExecuteReaderAsync(context, """
                SELECT word_key, meaning_key, stability, due_at
                FROM vocab_memory_card WHERE student_id = {0}
                """, [studentId], cancellationToken).ConfigureAwait(false);
            var cards = new List<MemoryCard>();
            var reader = rows.DbDataReader;
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cards.Add(new MemoryCard
                {
                    StudentId = studentId,
                    WordKey = reader.GetString(0),
                    MeaningKey = reader.GetString(1),
                    Stability = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                    DueAt = reader.GetFieldValue<DateTimeOffset>(3),
                });
            }
            return cards;
        }

        // No commit or SaveChanges: disposing rolls back the read-only transaction on every path.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
