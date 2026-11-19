using System.Buffers;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Lexarbor.Learning.Database.Repositories;

public sealed class VocabularyPlanStatsRepository(LearningDbContext context) : IVocabularyPlanStatsRepository
{
    public async Task<IVocabularyPlanStatsSnapshot> OpenAsync(IReadOnlyList<Guid> planIds, CancellationToken cancellationToken = default)
    {
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
            var plans = new List<VocabPlanModel>();
            var reactivations = new Dictionary<Guid, VocabularyPlanReactivation>();
            // One plan query also reads each plan's latest reactivate and the latest complete before it (ADR-0019 continuous plans).
            await using (var rows = await ExecuteReaderAsync(context, """
                SELECT p.id, p.student_id, p.book_id, p.unit_ids, p.segments, p.status::integer, p.total_words, p.created_at, p.updated_at,
                    r.created_at, c.created_at, p.version
                FROM vocab_plan p
                LEFT JOIN LATERAL (SELECT max(created_at) AS created_at FROM vocab_plan_change
                    WHERE plan_id = p.id AND action = 'reactivate') r ON true
                LEFT JOIN LATERAL (SELECT max(created_at) AS created_at FROM vocab_plan_change
                    WHERE plan_id = p.id AND action = 'complete' AND created_at <= r.created_at) c ON true
                WHERE p.id = ANY({0})
                """, new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = planIds.ToArray() }, cancellationToken).ConfigureAwait(false))
            {
                var reader = rows.DbDataReader;
                while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = reader.GetGuid(0);
                    plans.Add(new VocabPlanModel
                    {
                        Id = id,
                        StudentId = reader.GetGuid(1),
                        Version = reader.GetInt32(11),
                        Segments = VocabPlanRepository.DeserializeSegments(reader.GetString(4), reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetString(3)),
                        Status = (VocabPlanStatus)reader.GetInt32(5),
                        TotalWords = reader.GetInt32(6),
                        CreatedAt = reader.GetFieldValue<DateTimeOffset>(7),
                        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(8),
                    });
                    if (!reader.IsDBNull(9) && !reader.IsDBNull(10))
                        reactivations.Add(id, new(reader.GetFieldValue<DateTimeOffset>(10), reader.GetFieldValue<DateTimeOffset>(9)));
                }
            }
            return new Snapshot(context, transaction, plans, reactivations);
        }
        catch { await transaction.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static Task<RelationalDataReader> ExecuteReaderAsync(LearningDbContext context, string sql,
        object parameter, CancellationToken cancellationToken)
    {
        var command = context.GetService<IRawSqlCommandBuilder>().Build(sql, [parameter]);
        return command.RelationalCommand.ExecuteReaderAsync(new RelationalCommandParameterObject(
            context.GetService<IRelationalConnection>(), command.ParameterValues, null, context,
            context.GetService<IRelationalCommandDiagnosticsLogger>()), cancellationToken);
    }

    private sealed class Snapshot(LearningDbContext context, IDbContextTransaction transaction,
        IReadOnlyList<VocabPlanModel> plans, IReadOnlyDictionary<Guid, VocabularyPlanReactivation> reactivations)
        : IVocabularyPlanStatsSnapshot
    {
        public IReadOnlyList<VocabPlanModel> Plans => plans;
        public IReadOnlyDictionary<Guid, VocabularyPlanReactivation> Reactivations => reactivations;
        public async Task<IReadOnlyDictionary<Guid, VocabularyPlanStatsCounts>> ReadCountsAsync(
            IReadOnlyList<VocabularyPlanStatsScope> scopes, CancellationToken cancellationToken = default)
        {
            // One request relation, not a plan-by-plan or key-by-key query. Project scope keys once.
            var keySets = new List<IReadOnlyList<CardKey>>();
            var indexes = new Dictionary<IReadOnlyList<CardKey>, int>();
            foreach (var scope in scopes)
                if (!indexes.ContainsKey(scope.Keys)) { indexes.Add(scope.Keys, keySets.Count); keySets.Add(scope.Keys); }
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject(); writer.WriteStartArray("plans");
                foreach (var scope in scopes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteStartObject(); writer.WriteString("plan_id", scope.PlanId);
                    writer.WriteString("student_id", scope.StudentId); writer.WriteNumber("scope_id", indexes[scope.Keys]);
                    writer.WriteString("start_date", scope.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    writer.WriteString("end_date", scope.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteStartArray("scopes");
                foreach (var keys in keySets)
                {
                    writer.WriteStartArray();
                    foreach (var key in keys)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteStartObject(); writer.WriteString("word_key", key.WordKey);
                        writer.WriteString("meaning_key", key.MeaningKey); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            var payload = Encoding.UTF8.GetString(buffer.WrittenSpan);
            var counts = new Dictionary<Guid, VocabularyPlanStatsCounts>();
            await using (var rows = await ExecuteReaderAsync(context, """
                WITH requested AS MATERIALIZED (
                    SELECT * FROM jsonb_to_recordset(CAST({0} AS jsonb)->'plans')
                        q(plan_id uuid, student_id uuid, scope_id integer)
                ), scope_keys AS MATERIALIZED (
                    SELECT (s.ordinality - 1)::integer AS scope_id, k.word_key, k.meaning_key
                    FROM jsonb_array_elements(CAST({0} AS jsonb)->'scopes') WITH ORDINALITY s(keys, ordinality)
                    CROSS JOIN LATERAL jsonb_to_recordset(s.keys) k(word_key text, meaning_key text)
                ), matching_cards AS MATERIALIZED (
                    SELECT q.student_id, q.scope_id, c.word_key, c.first_learned_plan_id
                    FROM (SELECT DISTINCT student_id, scope_id FROM requested) q
                    JOIN scope_keys k ON k.scope_id = q.scope_id
                    JOIN vocab_memory_card c ON c.student_id = q.student_id
                        AND c.word_key = k.word_key AND c.meaning_key = k.meaning_key
                )
                SELECT q.plan_id, count(DISTINCT c.word_key)::integer,
                    count(DISTINCT c.word_key) FILTER (WHERE c.first_learned_plan_id IS NOT NULL
                        AND c.first_learned_plan_id <> q.plan_id)::integer
                FROM requested q JOIN matching_cards c ON c.student_id = q.student_id AND c.scope_id = q.scope_id
                GROUP BY q.plan_id
                """, payload, cancellationToken).ConfigureAwait(false))
            {
                var reader = rows.DbDataReader;
                while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                    counts.Add(reader.GetGuid(0), new(reader.GetInt32(1), reader.GetInt32(2), 0));
            }
            await using (var rows = await ExecuteReaderAsync(context, """
                WITH requested AS MATERIALIZED (
                    SELECT * FROM jsonb_to_recordset(CAST({0} AS jsonb)->'plans')
                        q(plan_id uuid, student_id uuid, start_date date, end_date date)
                ), completed_dates AS (
                    SELECT q.plan_id, d.local_date
                    FROM requested q JOIN vocab_plan_day d ON d.plan_id = q.plan_id
                        AND d.local_date BETWEEN q.start_date AND q.end_date AND d.completed_at IS NOT NULL
                    UNION
                    SELECT q.plan_id, (day->>'date')::date
                    FROM requested q JOIN vocab_plan p ON p.id=q.plan_id AND p.status=1
                    JOIN vocab_plan_legacy_history h ON h.student_id=q.student_id
                    CROSS JOIN LATERAL jsonb_array_elements(h.completed_days) day
                    WHERE (day->>'date')::date <= q.end_date
                )
                SELECT plan_id, count(*)::integer FROM completed_dates GROUP BY plan_id
                """, payload, cancellationToken).ConfigureAwait(false))
            {
                var reader = rows.DbDataReader;
                while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = reader.GetGuid(0);
                    var previous = counts.GetValueOrDefault(id) ?? new VocabularyPlanStatsCounts(0, 0, 0);
                    counts[id] = previous with { CheckInDays = reader.GetInt32(1) };
                }
            }
            return counts;
        }
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
