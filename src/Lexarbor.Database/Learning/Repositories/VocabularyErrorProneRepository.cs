using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

namespace Lexarbor.Learning.Database.Repositories;

public sealed class VocabularyErrorProneRepository(LearningDbContext context) : IVocabularyErrorProneRepository
{
    public async Task<IVocabularyErrorProneSnapshot> OpenAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
            await using var rows = await ExecuteReaderAsync(context, "SELECT id FROM learner_identity WHERE id = {0}", [studentId], cancellationToken).ConfigureAwait(false);
            var exists = await rows.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new Snapshot(context, transaction, studentId, exists);
        }
        catch { await transaction.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static Task<RelationalDataReader> ExecuteReaderAsync(LearningDbContext context, string sql,
        object[] parameters, CancellationToken cancellationToken)
    {
        var command = context.GetService<IRawSqlCommandBuilder>().Build(sql, parameters);
        return command.RelationalCommand.ExecuteReaderAsync(new RelationalCommandParameterObject(
            context.GetService<IRelationalConnection>(), command.ParameterValues, null, context,
            context.GetService<IRelationalCommandDiagnosticsLogger>()), cancellationToken);
    }

    private sealed class Snapshot(LearningDbContext context, IDbContextTransaction transaction, Guid studentId, bool exists) : IVocabularyErrorProneSnapshot
    {
        public bool StudentExists => exists;
        public async Task<VocabularyHistoryCoverage> ReadCoverageAsync(DateTimeOffset now, DateTimeOffset windowStart,
            CancellationToken cancellationToken = default)
        {
            await using var rows = await ExecuteReaderAsync(context, """
                WITH logs AS MATERIALIZED (
                    SELECT reviewed_at, COALESCE(state_before BETWEEN 0 AND 3 AND state_after BETWEEN 1 AND 3, false) AS known
                    FROM vocab_review_log WHERE student_id = {0} AND reviewed_at <= {1}
                ), summary AS (
                    SELECT max(reviewed_at) FILTER (WHERE NOT known) AS last_unknown,
                        count(*) FILTER (WHERE known AND reviewed_at >= {2}) AS known_count,
                        count(*) FILTER (WHERE NOT known AND reviewed_at >= {2}) AS unknown_count FROM logs
                )
                SELECT (SELECT min(reviewed_at) FROM logs WHERE known AND (s.last_unknown IS NULL OR reviewed_at > s.last_unknown)),
                    (SELECT min(a.answered_at) FROM vocab_attempt a JOIN vocab_issued_question q ON q.id = a.issued_question_id
                        WHERE q.student_id = {0} AND a.answered_at <= {1}), s.known_count, s.unknown_count
                FROM summary s
                """, [studentId, now, windowStart], cancellationToken).ConfigureAwait(false);
            await rows.ReadAsync(cancellationToken).ConfigureAwait(false);
            var reader = rows.DbDataReader;
            return new(Date(reader, 0), Date(reader, 1), reader.GetInt64(2), reader.GetInt64(3));
        }

        public async Task<IReadOnlyList<VocabularyErrorProneRow>> ReadCardsAsync(DateTimeOffset now, DateTimeOffset windowStart,
            CancellationToken cancellationToken = default)
        {
            await using var rows = await ExecuteReaderAsync(context, """
                WITH logs AS (
                    SELECT word_key COLLATE "C" AS word_key, meaning_key COLLATE "C" AS meaning_key, reviewed_at, state_before, state_after,
                        COALESCE(state_before BETWEEN 0 AND 3 AND state_after BETWEEN 1 AND 3, false) AS known
                    FROM vocab_review_log WHERE student_id = {0} AND reviewed_at <= {1}
                ), marked AS (
                    SELECT *, max(reviewed_at) FILTER (WHERE NOT known) OVER (PARTITION BY word_key, meaning_key) AS last_unknown FROM logs
                ), states AS (
                    SELECT word_key, meaning_key,
                        min(reviewed_at) FILTER (WHERE known AND (last_unknown IS NULL OR reviewed_at > last_unknown)) AS coverage_start,
                        count(*) FILTER (WHERE known AND reviewed_at >= {2}) AS known_count,
                        count(*) FILTER (WHERE NOT known AND reviewed_at >= {2}) AS unknown_count,
                        count(*) FILTER (WHERE state_before = 2 AND state_after = 3 AND reviewed_at >= {2}) AS forget_count
                    FROM marked GROUP BY word_key, meaning_key
                ), answers AS (
                    SELECT q.word_key COLLATE "C" AS word_key, q.meaning_key COLLATE "C" AS meaning_key, min(a.answered_at) AS coverage_start, max(a.answered_at) AS last_answered,
                        count(*) FILTER (WHERE a.answered_at >= {2}) AS attempts,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND a.result IN (2,3)) AS errors,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 1) AS en_attempts,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 1 AND a.result IN (2,3)) AS en_errors,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 2) AS zh_attempts,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 2 AND a.result IN (2,3)) AS zh_errors,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 3) AS fill_attempts,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 3 AND a.result IN (2,3)) AS fill_errors,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 7) AS pos_attempts,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 7 AND a.result IN (2,3)) AS pos_errors,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 4) AS form_attempts,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 4 AND a.result IN (2,3)) AS form_errors,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 5) AS relation_attempts,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 5 AND a.result IN (2,3)) AS relation_errors,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 6) AS form_fill_attempts,
                        count(*) FILTER (WHERE a.answered_at >= {2} AND q.direction = 6 AND a.result IN (2,3)) AS form_fill_errors
                    FROM vocab_attempt a JOIN vocab_issued_question q ON q.id = a.issued_question_id
                    WHERE q.student_id = {0} AND a.answered_at <= {1} GROUP BY q.word_key COLLATE "C", q.meaning_key COLLATE "C"
                )
                SELECT c.word_key, c.meaning_key, c.stability,
                    COALESCE(a.attempts,0), COALESCE(a.errors,0), COALESCE(a.en_attempts,0), COALESCE(a.en_errors,0),
                    COALESCE(a.zh_attempts,0), COALESCE(a.zh_errors,0), COALESCE(a.fill_attempts,0), COALESCE(a.fill_errors,0), a.last_answered,
                    COALESCE(s.forget_count,0), s.coverage_start, a.coverage_start, COALESCE(s.known_count,0), COALESCE(s.unknown_count,0),
                    COALESCE(a.pos_attempts,0), COALESCE(a.pos_errors,0), COALESCE(a.form_attempts,0), COALESCE(a.form_errors,0),
                    COALESCE(a.relation_attempts,0), COALESCE(a.relation_errors,0), COALESCE(a.form_fill_attempts,0), COALESCE(a.form_fill_errors,0)
                FROM vocab_memory_card c
                LEFT JOIN states s ON s.word_key = c.word_key AND s.meaning_key = c.meaning_key
                LEFT JOIN answers a ON a.word_key = c.word_key AND a.meaning_key = c.meaning_key
                WHERE c.student_id = {0}
                """, [studentId, now, windowStart], cancellationToken).ConfigureAwait(false);
            var result = new List<VocabularyErrorProneRow>();
            var reader = rows.DbDataReader;
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDouble(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
                    reader.GetInt64(9), reader.GetInt64(10), Date(reader, 11), reader.GetInt64(12),
                    new(Date(reader, 13), Date(reader, 14), reader.GetInt64(15), reader.GetInt64(16)),
                    null, null, null, null, null, reader.GetInt64(17), reader.GetInt64(18),
                    reader.GetInt64(19), reader.GetInt64(20), reader.GetInt64(21), reader.GetInt64(22), reader.GetInt64(23), reader.GetInt64(24)));
            return result;
        }
        public Task<IReadOnlyDictionary<CardKey, VocabularyErrorProneDisplay>> ReadSnapshotsAsync(IReadOnlyList<CardKey> keys,
            DateTimeOffset now, CancellationToken cancellationToken = default) =>
            VocabularyQuestionSnapshots.ReadAsync(context, studentId, keys, now, cancellationToken);
        private static DateTimeOffset? Date(System.Data.Common.DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
