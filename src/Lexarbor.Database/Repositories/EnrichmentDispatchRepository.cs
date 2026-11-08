using System.Text.Json;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed partial class EnrichmentSchedulerRepository
{
    // Bound each lane after word deduplication, not before it. Dependencies that
    // cannot advance do not displace ready work or crashed owners needing recovery.
    // Separate NULL/non-NULL prerequisite lookups use the existing partial unique
    // indexes; an IS-to-IS self-join would scan all targets for every due round.
    public async Task<IReadOnlyList<EnrichmentDispatch>> PlanAsync(int limit, long now,
        IReadOnlyList<string> excludedWords, CancellationToken token, bool expansionReady = true)
    {
        if (limit is < 1 or > 1000 || excludedWords.Count > 4096) throw new ArgumentOutOfRangeException(nameof(limit));
        const string sql = """
            WITH recent AS (
              SELECT id AS word_id FROM (SELECT id FROM vocabulary ORDER BY rowid DESC LIMIT @limit)
              UNION SELECT vocabulary_id FROM (SELECT vocabulary_id FROM vocabulary_meaning ORDER BY rowid DESC LIMIT @limit)
            ), candidates AS (
              SELECT t.id AS Id,t.word_id AS WordId,t.step AS Step,r.updated_at_unix_ms AS QueuedAtUnixMs,
                EXISTS (SELECT 1 FROM recent n WHERE n.word_id=t.word_id) AS Recent,
                CASE
                  WHEN r.state='Running' OR EXISTS (SELECT 1 FROM enrichment_call c
                    WHERE c.target_id=t.id AND c.round=r.number AND c.result='Started') THEN 'Recovery'
                  WHEN t.step='Expand' THEN CASE WHEN r.plan_json IS NULL THEN 'Expansion' ELSE 'Ready' END
                  WHEN t.step='Prepare' THEN CASE WHEN r.plan_json IS NULL THEN 'Text' ELSE 'Ready' END
                  WHEN t.step='Audio' AND EXISTS (SELECT 1 FROM vocabulary_audio_binding b
                    WHERE b.word_id=t.word_id AND b.meaning_id IS t.meaning_id AND b.locale=t.locale) THEN 'Ready'
                  WHEN p.state IN ('Succeeded','Failed') THEN 'Ready'
                  WHEN p.target_id IS NULL OR (p.state IN ('Pending','Waiting')
                    AND (p.next_attempt_at_unix_ms IS NULL OR p.next_attempt_at_unix_ms<=@now)
                    AND NOT EXISTS (SELECT 1 FROM enrichment_call c WHERE c.target_id=pt.id AND c.round=p.number AND c.result='Started'))
                    THEN CASE WHEN p.plan_json IS NULL THEN 'Text' ELSE 'Ready' END
                  ELSE NULL END AS Lane
              FROM enrichment_target t JOIN enrichment_round r ON r.target_id=t.id AND r.number=t.current_round
              LEFT JOIN enrichment_target pt ON pt.id=CASE WHEN t.step IN ('Fields','Audio') THEN
                CASE WHEN t.meaning_id IS NULL THEN
                  (SELECT id FROM enrichment_target WHERE word_id=t.word_id AND meaning_id IS NULL
                    AND locale=t.locale AND step='Prepare' AND NOT deleted)
                ELSE
                  (SELECT id FROM enrichment_target WHERE word_id=t.word_id AND meaning_id IS NOT NULL
                    AND meaning_id=t.meaning_id AND locale=t.locale AND step='Prepare' AND NOT deleted)
                END END
              LEFT JOIN enrichment_round p ON p.target_id=pt.id AND p.number=pt.current_round
              WHERE (@expansionReady OR t.step!='Expand') AND NOT t.deleted AND r.consumed_failures<r.failure_budget
                AND (r.state IN ('Pending','Waiting') OR r.state='Running' AND r.lease_expires_at_unix_ms<=@now)
                AND (r.next_attempt_at_unix_ms IS NULL OR r.next_attempt_at_unix_ms<=@now)
                AND t.word_id NOT IN (SELECT value FROM json_each(@excluded))
            ), words AS (
              SELECT *,row_number() OVER (PARTITION BY Lane,WordId ORDER BY
                CASE WHEN Lane='Text' AND Step='Prepare' THEN 0 WHEN Lane='Text' AND Step='Fields' THEN 1 ELSE 2 END,
                QueuedAtUnixMs,Id) AS WordRank
              FROM candidates WHERE Lane IS NOT NULL
            ), ranked AS (
              SELECT *,row_number() OVER (PARTITION BY Lane,Recent ORDER BY QueuedAtUnixMs,Id) AS Position
              FROM words WHERE WordRank=1
            ) SELECT Id,WordId,Step,Lane,QueuedAtUnixMs,Recent FROM ranked WHERE Position<=@limit
            """;
        var rows = await context.Database.SqlQueryRaw<DispatchRow>(sql, new SqliteParameter("@limit", limit),
            new SqliteParameter("@now", now), new SqliteParameter("@expansionReady", expansionReady), new SqliteParameter("@excluded", JsonSerializer.Serialize(excludedWords))).ToArrayAsync(token);
        var result = new List<EnrichmentDispatch>();
        foreach (var lane in new[] { "Recovery", "Ready", "Text", "Expansion" })
        {
            var ordered = rows.Where(r => r.Lane == lane).OrderBy(r => r.QueuedAtUnixMs).ThenBy(r => r.Id).ToArray();
            var recent = ordered.Where(r => r.Recent).Take(limit / 2).ToArray();
            var selected = recent.Concat(ordered.Except(recent).Take(limit - recent.Length));
            result.AddRange(selected.Select(r => new EnrichmentDispatch(r.Id, r.WordId, r.Step, r.Lane, r.QueuedAtUnixMs)));
        }
        return result;
    }

    private sealed class DispatchRow
    {
        public string Id { get; set; } = "";
        public string WordId { get; set; } = "";
        public string Step { get; set; } = "";
        public string Lane { get; set; } = "";
        public long QueuedAtUnixMs { get; set; }
        public bool Recent { get; set; }
    }
}
