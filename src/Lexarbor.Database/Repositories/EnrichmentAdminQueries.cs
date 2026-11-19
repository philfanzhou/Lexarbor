using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lexarbor.Database.Repositories;

// Shared current-demand predicate for read-only reporting AND the retry writer.
// A specific target needs its own binding even when playback can fall back.
internal static class EnrichmentAdminQueries
{
    // Match String.IsNullOrWhiteSpace for persisted legacy whitespace too: the
    // characters btrim() removes are exactly .NET's Unicode white space.
    private const string WhiteSpace = "chr(9)||chr(10)||chr(11)||chr(12)||chr(13)||chr(32)||chr(133)||chr(160)||chr(5760)||chr(8192)||chr(8193)||chr(8194)||chr(8195)||chr(8196)||chr(8197)||chr(8198)||chr(8199)||chr(8200)||chr(8201)||chr(8202)||chr(8232)||chr(8233)||chr(8239)||chr(8287)||chr(12288)";

    private static string DemandCte(bool perTarget) => $$"""
        WITH locales(locale) AS (VALUES ('en-GB'), ('en-US')),
        defaults AS (
          SELECT w.id AS word_id, w.word, l.locale,
            NOT (EXISTS (SELECT 1 FROM vocabulary_meaning m JOIN vocabulary_meaning_unit p ON p.meaning_id=m.id WHERE m.vocabulary_id=w.id)
              AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning m JOIN vocabulary_meaning_unit p ON p.meaning_id=m.id WHERE m.vocabulary_id=w.id AND p.entry_kind!='phrase')) AS fields_applicable,
            (btrim(coalesce(CASE l.locale WHEN 'en-GB' THEN w.phonetic_uk ELSE w.phonetic_us END,''),{{WhiteSpace}})='' OR
              EXISTS (SELECT 1 FROM vocabulary_meaning m WHERE m.vocabulary_id=w.id AND btrim(coalesce(m.part_of_speech,''),{{WhiteSpace}})=''
                AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning_unit p WHERE p.meaning_id=m.id AND p.entry_kind='phrase'))) AS fields_missing
          FROM vocabulary w CROSS JOIN locales l {{(perTarget ? "WHERE w.id=t.word_id" : "")}}
        ), demands AS (
          SELECT word_id, word, NULL AS meaning_id, NULL AS meaning, locale, 'Fields' AS step, fields_missing AS missing
          FROM defaults d WHERE fields_applicable AND NOT EXISTS (
            SELECT 1 FROM enrichment_target ft JOIN enrichment_round fr ON fr.target_id=ft.id AND fr.number=ft.current_round
            WHERE ft.word_id=d.word_id AND ft.meaning_id IS NULL AND ft.locale=d.locale AND ft.step='Fields'
              AND NOT ft.deleted AND fr.state='Succeeded' AND fr.fields_not_required)
          UNION ALL
          SELECT d.word_id,d.word,NULL,NULL,d.locale,'Audio', NOT EXISTS
            (SELECT 1 FROM vocabulary_audio_binding b WHERE b.word_id=d.word_id AND b.meaning_id IS NULL AND b.locale=d.locale)
          FROM defaults d
          UNION ALL
          SELECT m.vocabulary_id,w.word,m.id,m.meaning,l.locale,'Audio', NOT EXISTS
            (SELECT 1 FROM vocabulary_audio_binding b WHERE b.word_id=m.vocabulary_id AND b.meaning_id=m.id AND b.locale=l.locale)
          FROM vocabulary_meaning m JOIN vocabulary w ON w.id=m.vocabulary_id CROSS JOIN locales l
          WHERE btrim(coalesce(CASE l.locale WHEN 'en-GB' THEN m.phonetic_uk ELSE m.phonetic_us END,''),{{WhiteSpace}})!=''
            {{(perTarget ? "AND w.id=t.word_id" : "")}}
          UNION ALL
          SELECT m.vocabulary_id,w.word,m.id,m.meaning,NULL,'Expand',NOT EXISTS
            (SELECT 1 FROM enrichment_target t JOIN enrichment_round r ON r.target_id=t.id AND r.number=t.current_round
              WHERE t.word_id=m.vocabulary_id AND t.meaning_id=m.id AND t.step='Expand' AND NOT t.deleted AND r.state='Succeeded')
          FROM vocabulary_meaning m JOIN vocabulary w ON w.id=m.vocabulary_id
          WHERE NOT EXISTS (SELECT 1 FROM enrichment_generated_word g WHERE g.word_id=w.id)
            AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning_unit p WHERE p.meaning_id=m.id AND p.entry_kind='phrase')
            {{(perTarget ? "AND w.id=t.word_id" : "")}}
        )
        """;
    private const string RoundColumns = """
        r.number AS Number, r.state AS RoundState, coalesce(r.request_count,0) AS RequestCount,
        coalesce(r.consumed_failures,0) AS ConsumedFailures, coalesce(r.failure_budget,@budget) AS FailureBudget,
        r.wait_reason AS StoredWaitReason, r.error_code AS ErrorCode, r.next_attempt_at_unix_ms AS StoredNext,
        r.recovery_id AS RecoveryId,coalesce(r.force_single,false) AS ForceSingle,coalesce(r.fields_not_required,false) AS FieldsNotRequired,
        r.state='Failed' AND (r.recovery_id IS NULL OR NOT EXISTS (SELECT 1 FROM enrichment_recovery receipt
          WHERE receipt.id=r.recovery_id AND right(receipt.configuration_version,length(@recoveryPolicy))=@recoveryPolicy))
          AND (r.error_code = ANY(@recoveryErrors)
          AND (r.error_code NOT IN ('tts.capability_unsupported','tts.prepared_plan_required') OR r.request_count=0)
          AND NOT EXISTS (SELECT 1 FROM enrichment_recovery ar WHERE ar.target_id=r.target_id AND ar.input_revision=r.input_revision
            AND ar.configuration_version=r.configuration_version||@recoveryPolicy)
          OR r.error_code='preparation.failed' AND EXISTS (
            SELECT 1 FROM enrichment_target root JOIN enrichment_round rr ON rr.target_id=root.id AND rr.number=root.current_round
            WHERE root.id=CASE WHEN t.meaning_id IS NULL THEN
              (SELECT matched.id FROM enrichment_target matched WHERE matched.word_id=t.word_id AND matched.meaning_id IS NULL
                AND matched.locale=t.locale AND matched.step='Prepare' AND NOT matched.deleted)
              ELSE (SELECT matched.id FROM enrichment_target matched WHERE matched.word_id=t.word_id AND matched.meaning_id IS NOT NULL
                AND matched.meaning_id=t.meaning_id AND matched.locale=t.locale AND matched.step='Prepare' AND NOT matched.deleted) END
              AND NOT root.deleted AND rr.state='Failed'
              AND (rr.recovery_id IS NULL OR NOT EXISTS (SELECT 1 FROM enrichment_recovery receipt
                WHERE receipt.id=rr.recovery_id AND right(receipt.configuration_version,length(@recoveryPolicy))=@recoveryPolicy))
              AND rr.error_code = ANY(@recoveryErrors)
              AND NOT EXISTS (SELECT 1 FROM enrichment_recovery ar WHERE ar.target_id=rr.target_id
                AND ar.input_revision=rr.input_revision AND ar.configuration_version=rr.configuration_version||@recoveryPolicy))) AS AutomaticRecoveryAvailable,
        coalesce(r.created_at_unix_ms,0) AS CreatedAtUnixMs, coalesce(r.updated_at_unix_ms,0) AS UpdatedAtUnixMs
        """;

    internal static IQueryable<EnrichmentAdminRow> Rows(VocabularyDbContext context, EnrichmentAdminConfiguration c, long now, bool tasks)
    {
        // For persisted tasks, compute both demand flags from the indexed word.
        // A shared catalog-wide demands CTE would be rescanned for every target.
        // For catalog reports, split default/specific target lookups so PostgreSQL
        // can use each partial unique index.
        var source = tasks ? $$"""
            SELECT t.id AS Id,t.word_id AS WordId,w.word AS Word,t.meaning_id AS MeaningId,m.meaning AS Meaning,t.locale AS Locale,t.step AS Step,
              NOT t.deleted AND demand.applicable AS Applicable, NOT t.deleted AND demand.missing AS Missing,
              t.step!='Expand' AND m.id IS NOT NULL AND btrim(coalesce(CASE t.locale WHEN 'en-GB' THEN m.phonetic_uk ELSE m.phonetic_us END,''),{{WhiteSpace}})='' AS InheritsDefault,
            {{RoundColumns}}
            FROM enrichment_target t LEFT JOIN vocabulary w ON w.id=t.word_id LEFT JOIN vocabulary_meaning m ON m.id=t.meaning_id AND m.vocabulary_id=t.word_id
            JOIN enrichment_round r ON r.target_id=t.id AND r.number=t.current_round
            LEFT JOIN LATERAL (
              {{DemandCte(perTarget: true)}}
              SELECT count(*) > 0 AS applicable, coalesce(bool_or(d.missing), false) AS missing
              FROM demands d
              WHERE d.meaning_id IS NOT DISTINCT FROM t.meaning_id AND d.locale IS NOT DISTINCT FROM t.locale
                AND (d.step=t.step OR t.step='Prepare' AND d.missing)
            ) demand ON true
            """ : $$"""
            SELECT t.id AS Id,d.word_id AS WordId,d.word AS Word,d.meaning_id AS MeaningId,d.meaning AS Meaning,d.locale AS Locale,d.step AS Step,
              true AS Applicable,d.missing AS Missing,false AS InheritsDefault,
            {{RoundColumns}}
            FROM demands d LEFT JOIN enrichment_target t ON t.id=CASE WHEN d.meaning_id IS NULL THEN
              (SELECT matched.id FROM enrichment_target matched
                WHERE matched.word_id=d.word_id AND matched.meaning_id IS NULL
                  AND matched.locale IS NOT DISTINCT FROM d.locale AND matched.step=d.step AND NOT matched.deleted)
              ELSE (SELECT matched.id FROM enrichment_target matched
                WHERE matched.word_id=d.word_id AND matched.meaning_id IS NOT NULL AND matched.meaning_id=d.meaning_id
                  AND matched.locale IS NOT DISTINCT FROM d.locale AND matched.step=d.step AND NOT matched.deleted) END
            LEFT JOIN enrichment_round r ON r.target_id=t.id AND r.number=t.current_round
            """;
        var sql = (tasks ? "WITH " : DemandCte(perTarget: false) + ", ") + "source AS (" + source + """
            ), availability AS (
              SELECT s.*,
                CASE WHEN Step IN ('Fields','Expand') THEN @fields WHEN Step='Audio' OR MeaningId IS NOT NULL THEN @audio ELSE @fields OR @audio END AS Enabled,
                CASE WHEN Step='Expand' THEN @prepare AND EXISTS (SELECT 1 FROM vocabulary_book b WHERE b.id=@book AND b.status) WHEN Step='Audio' THEN CASE Locale WHEN 'en-GB' THEN @uk ELSE @us END ELSE @prepare END AS Configured,
                CASE WHEN Step IN ('Fields','Expand') OR Step='Prepare' AND MeaningId IS NULL AND @fields THEN true ELSE @storage END AS StorageReady,
                CASE WHEN Step='Audio' THEN ts.blocked ELSE txt.blocked END AS Blocked,
                coalesce(txt.blocked,false) OR Step='Audio' AND coalesce(ts.blocked,false) AS RecoveryBlocked,
                CASE WHEN Step='Audio' THEN ts.paused ELSE txt.paused END AS Paused,
                CASE WHEN Step='Audio' THEN ts.next_attempt_at_unix_ms ELSE txt.next_attempt_at_unix_ms END AS ServiceNext
              FROM source s
              LEFT JOIN enrichment_service txt ON txt.id=@textId AND txt.configuration_version=@textVersion
              LEFT JOIN enrichment_service ts ON ts.id=@ttsId AND ts.configuration_version=@ttsVersion
            ), reasons AS (
              SELECT a.*, CASE
                WHEN NOT Applicable OR NOT Missing OR RoundState='Failed' THEN NULL
                WHEN NOT Enabled THEN 'ConfigurationMissing'
                WHEN NOT StorageReady THEN 'StorageDisabled'
                WHEN NOT Configured OR Blocked THEN 'ConfigurationMissing'
                WHEN Paused THEN 'ServicePaused'
                WHEN ServiceNext>@now THEN 'Throttled'
                WHEN RoundState='Running' THEN NULL
                WHEN StoredWaitReason='ConfigurationMissing' AND Step IN ('Fields','Audio') THEN 'PreparationPending'
                ELSE StoredWaitReason END AS WaitReason
              FROM availability a
            )
            SELECT Id AS "Id",WordId AS "WordId",Word AS "Word",MeaningId AS "MeaningId",Meaning AS "Meaning",Locale AS "Locale",Step AS "Step",
              Applicable AS "Applicable",Missing AS "Missing",InheritsDefault AS "InheritsDefault",
              coalesce(Number,0) AS "Number",RoundState AS "RoundState",RequestCount AS "RequestCount",ConsumedFailures AS "ConsumedFailures",
              FailureBudget AS "FailureBudget",ErrorCode AS "ErrorCode",CreatedAtUnixMs AS "CreatedAtUnixMs",UpdatedAtUnixMs AS "UpdatedAtUnixMs",
              WaitReason AS "WaitReason",RecoveryId AS "RecoveryId",ForceSingle AS "ForceSingle",FieldsNotRequired AS "FieldsNotRequired",
              coalesce(AutomaticRecoveryAvailable,false) AND Applicable AND Missing AND Enabled AND Configured AND StorageReady
                AND @prepare AND NOT RecoveryBlocked AS "AutomaticRecoveryAvailable",
              CASE WHEN NOT Applicable THEN 'Cancelled' WHEN NOT Missing THEN 'Succeeded' WHEN RoundState='Failed' THEN 'Failed'
                WHEN WaitReason IS NOT NULL THEN 'Waiting' WHEN RoundState='Running' THEN 'Running' WHEN Step='Prepare' AND RoundState='Succeeded' THEN 'Succeeded' ELSE 'Pending' END AS "State",
              CASE WHEN WaitReason IN ('ServicePaused','Throttled') AND ServiceNext>coalesce(StoredNext,0) THEN ServiceNext ELSE StoredNext END AS "NextAttemptAtUnixMs"
            FROM reasons
            """;
        object[] parameters = [new NpgsqlParameter("@book",c.ExpansionBookId ?? ""), new NpgsqlParameter("@budget",c.FailureBudget),new NpgsqlParameter("@fields",c.FieldsEnabled),
            new NpgsqlParameter("@audio",c.AudioEnabled),new NpgsqlParameter("@uk",c.CanTtsUk),new NpgsqlParameter("@us",c.CanTtsUs),
            new NpgsqlParameter("@prepare",c.CanPrepare),new NpgsqlParameter("@storage",c.StorageEnabled),new NpgsqlParameter("@now",now),
            new NpgsqlParameter("@textId",c.TextServiceId),new NpgsqlParameter("@textVersion",c.TextServiceVersion),
            new NpgsqlParameter("@ttsId",c.TtsServiceId),new NpgsqlParameter("@ttsVersion",c.TtsServiceVersion),
            new NpgsqlParameter("@recoveryErrors",EnrichmentRecoveryPolicy.Recoverable.ToArray()),
            new NpgsqlParameter("@recoveryPolicy",EnrichmentRecoveryPolicy.ReceiptSuffix)];
        return context.Database.SqlQueryRaw<EnrichmentAdminRow>(sql, parameters);
    }
}

internal sealed class EnrichmentAdminRow
{
    public string? Id { get; set; }
    public string WordId { get; set; } = "";
    public string? Word { get; set; }
    public string? MeaningId { get; set; }
    public string? Meaning { get; set; }
    public string? Locale { get; set; }
    public string Step { get; set; } = "";
    public bool Applicable { get; set; }
    public bool Missing { get; set; }
    public bool InheritsDefault { get; set; }
    public int Number { get; set; }
    public string? RoundState { get; set; }
    public string State { get; set; } = "";
    public int RequestCount { get; set; }
    public int ConsumedFailures { get; set; }
    public int FailureBudget { get; set; }
    public string? WaitReason { get; set; }
    public string? ErrorCode { get; set; }
    public string? RecoveryId { get; set; }
    public bool ForceSingle { get; set; }
    public bool FieldsNotRequired { get; set; }
    public bool AutomaticRecoveryAvailable { get; set; }
    public long? NextAttemptAtUnixMs { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}
