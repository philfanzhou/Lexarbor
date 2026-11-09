using System.Text.Json;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

// Shared current-demand predicate for read-only reporting AND the retry writer.
// A specific target needs its own binding even when playback can fall back.
internal static class EnrichmentAdminQueries
{
    // Match String.IsNullOrWhiteSpace for persisted legacy whitespace too.
    private static string DemandCte(bool scoped) => $$"""
        WITH locales(locale) AS (VALUES ('en-GB'), ('en-US')),
        defaults AS (
          SELECT w.id AS word_id, w.word, l.locale,
            NOT (EXISTS (SELECT 1 FROM vocabulary_meaning m JOIN vocabulary_meaning_unit p ON p.meaning_id=m.id WHERE m.vocabulary_id=w.id)
              AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning m JOIN vocabulary_meaning_unit p ON p.meaning_id=m.id WHERE m.vocabulary_id=w.id AND p.entry_kind!='phrase')) AS fields_applicable,
            (trim(coalesce(CASE l.locale WHEN 'en-GB' THEN w.phonetic_uk ELSE w.phonetic_us END,''),char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288))='' OR
              EXISTS (SELECT 1 FROM vocabulary_meaning m WHERE m.vocabulary_id=w.id AND trim(coalesce(m.part_of_speech,''),char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288))=''
                AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning_unit p WHERE p.meaning_id=m.id AND p.entry_kind='phrase'))) AS fields_missing
          FROM vocabulary w CROSS JOIN locales l {{(scoped ? "WHERE w.id=@demandWord" : "")}}
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
          WHERE trim(coalesce(CASE l.locale WHEN 'en-GB' THEN m.phonetic_uk ELSE m.phonetic_us END,''),char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288))!=''
            {{(scoped ? "AND w.id=@demandWord" : "")}}
          UNION ALL
          SELECT m.vocabulary_id,w.word,m.id,m.meaning,NULL,'Expand',NOT EXISTS
            (SELECT 1 FROM enrichment_target t JOIN enrichment_round r ON r.target_id=t.id AND r.number=t.current_round
              WHERE t.word_id=m.vocabulary_id AND t.meaning_id=m.id AND t.step='Expand' AND NOT t.deleted AND r.state='Succeeded')
          FROM vocabulary_meaning m JOIN vocabulary w ON w.id=m.vocabulary_id
          WHERE NOT EXISTS (SELECT 1 FROM enrichment_generated_word g WHERE g.word_id=w.id)
            AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning_unit p WHERE p.meaning_id=m.id AND p.entry_kind='phrase')
            {{(scoped ? "AND w.id=@demandWord" : "")}}
        )
        """;
    private const string RoundColumns = """
        r.number AS Number, r.state AS RoundState, coalesce(r.request_count,0) AS RequestCount,
        coalesce(r.consumed_failures,0) AS ConsumedFailures, coalesce(r.failure_budget,@budget) AS FailureBudget,
        r.wait_reason AS StoredWaitReason, r.error_code AS ErrorCode, r.next_attempt_at_unix_ms AS StoredNext,
        r.recovery_id AS RecoveryId,coalesce(r.force_single,0) AS ForceSingle,coalesce(r.fields_not_required,0) AS FieldsNotRequired,
        r.state='Failed' AND (r.recovery_id IS NULL OR NOT EXISTS (SELECT 1 FROM enrichment_recovery receipt
          WHERE receipt.id=r.recovery_id AND substr(receipt.configuration_version,-length(@recoveryPolicy))=@recoveryPolicy))
          AND (r.error_code IN (SELECT value FROM json_each(@recoveryErrors))
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
                WHERE receipt.id=rr.recovery_id AND substr(receipt.configuration_version,-length(@recoveryPolicy))=@recoveryPolicy))
              AND rr.error_code IN (SELECT value FROM json_each(@recoveryErrors))
              AND NOT EXISTS (SELECT 1 FROM enrichment_recovery ar WHERE ar.target_id=rr.target_id
                AND ar.input_revision=rr.input_revision AND ar.configuration_version=rr.configuration_version||@recoveryPolicy))) AS AutomaticRecoveryAvailable,
        coalesce(r.created_at_unix_ms,0) AS CreatedAtUnixMs, coalesce(r.updated_at_unix_ms,0) AS UpdatedAtUnixMs
        """;

    internal static IQueryable<EnrichmentAdminRow> Rows(VocabularyDbContext context, EnrichmentAdminConfiguration c, long now, bool tasks,
        string? wordId = null)
    {
        // Split default/specific lookups so SQLite can use the partial unique
        // indexes. A null-safe multi-column join can choose an automatic index
        // without word_id and repeatedly scan all default targets per demand,
        // holding the report's read transaction open against writer commits.
        var source = tasks ? """
            SELECT t.id AS Id,t.word_id AS WordId,w.word AS Word,t.meaning_id AS MeaningId,m.meaning AS Meaning,t.locale AS Locale,t.step AS Step,
              NOT t.deleted AND EXISTS (SELECT 1 FROM demands d WHERE d.word_id=t.word_id AND d.meaning_id IS t.meaning_id AND d.locale IS t.locale
                AND (d.step=t.step OR t.step='Prepare' AND d.missing)) AS Applicable,
              NOT t.deleted AND EXISTS (SELECT 1 FROM demands d WHERE d.word_id=t.word_id AND d.meaning_id IS t.meaning_id AND d.locale IS t.locale
                AND (d.step=t.step OR t.step='Prepare') AND d.missing) AS Missing,
              t.step!='Expand' AND m.id IS NOT NULL AND trim(coalesce(CASE t.locale WHEN 'en-GB' THEN m.phonetic_uk ELSE m.phonetic_us END,''),char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288))='' AS InheritsDefault,
            """ + RoundColumns + "\n" + """
            FROM enrichment_target t LEFT JOIN vocabulary w ON w.id=t.word_id LEFT JOIN vocabulary_meaning m ON m.id=t.meaning_id AND m.vocabulary_id=t.word_id
            JOIN enrichment_round r ON r.target_id=t.id AND r.number=t.current_round
            """ : """
            SELECT t.id AS Id,d.word_id AS WordId,d.word AS Word,d.meaning_id AS MeaningId,d.meaning AS Meaning,d.locale AS Locale,d.step AS Step,
              1 AS Applicable,d.missing AS Missing,0 AS InheritsDefault,
            """ + RoundColumns + "\n" + """
            FROM demands d LEFT JOIN enrichment_target t ON t.id=CASE WHEN d.meaning_id IS NULL THEN
              (SELECT matched.id FROM enrichment_target matched
                WHERE matched.word_id=d.word_id AND matched.meaning_id IS NULL
                  AND matched.locale IS d.locale AND matched.step=d.step AND NOT matched.deleted)
              ELSE (SELECT matched.id FROM enrichment_target matched
                WHERE matched.word_id=d.word_id AND matched.meaning_id IS NOT NULL AND matched.meaning_id=d.meaning_id
                  AND matched.locale IS d.locale AND matched.step=d.step AND NOT matched.deleted) END
            LEFT JOIN enrichment_round r ON r.target_id=t.id AND r.number=t.current_round
            """;
        var sql = DemandCte(wordId != null) + ", source AS (" + source + """
            ), availability AS (
              SELECT s.*,
                CASE WHEN Step IN ('Fields','Expand') THEN @fields WHEN Step='Audio' OR MeaningId IS NOT NULL THEN @audio ELSE @fields OR @audio END AS Enabled,
                CASE WHEN Step='Expand' THEN @prepare AND EXISTS (SELECT 1 FROM vocabulary_book b WHERE b.id=@book AND b.status) WHEN Step='Audio' THEN CASE Locale WHEN 'en-GB' THEN @uk ELSE @us END ELSE @prepare END AS Configured,
                CASE WHEN Step IN ('Fields','Expand') OR Step='Prepare' AND MeaningId IS NULL AND @fields THEN 1 ELSE @storage END AS StorageReady,
                CASE WHEN Step='Audio' THEN ts.blocked ELSE txt.blocked END AS Blocked,
                coalesce(txt.blocked,0) OR Step='Audio' AND coalesce(ts.blocked,0) AS RecoveryBlocked,
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
            SELECT Id,WordId,Word,MeaningId,Meaning,Locale,Step,Applicable,Missing,InheritsDefault,
              coalesce(Number,0) AS Number,RoundState,RequestCount,ConsumedFailures,FailureBudget,ErrorCode,CreatedAtUnixMs,UpdatedAtUnixMs,WaitReason,
              RecoveryId,ForceSingle,FieldsNotRequired,
              coalesce(AutomaticRecoveryAvailable,0) AND Applicable AND Missing AND Enabled AND Configured AND StorageReady
                AND @prepare AND NOT RecoveryBlocked AS AutomaticRecoveryAvailable,
              CASE WHEN NOT Applicable THEN 'Cancelled' WHEN NOT Missing THEN 'Succeeded' WHEN RoundState='Failed' THEN 'Failed'
                WHEN WaitReason IS NOT NULL THEN 'Waiting' WHEN RoundState='Running' THEN 'Running' WHEN Step='Prepare' AND RoundState='Succeeded' THEN 'Succeeded' ELSE 'Pending' END AS State,
              CASE WHEN WaitReason IN ('ServicePaused','Throttled') AND ServiceNext>coalesce(StoredNext,0) THEN ServiceNext ELSE StoredNext END AS NextAttemptAtUnixMs
            FROM reasons
            """;
        object[] parameters = [new SqliteParameter("@book",c.ExpansionBookId ?? ""), new SqliteParameter("@budget",c.FailureBudget),new SqliteParameter("@fields",c.FieldsEnabled),
            new SqliteParameter("@audio",c.AudioEnabled),new SqliteParameter("@uk",c.CanTtsUk),new SqliteParameter("@us",c.CanTtsUs),
            new SqliteParameter("@prepare",c.CanPrepare),new SqliteParameter("@storage",c.StorageEnabled),new SqliteParameter("@now",now),
            new SqliteParameter("@textId",c.TextServiceId),new SqliteParameter("@textVersion",c.TextServiceVersion),
            new SqliteParameter("@ttsId",c.TtsServiceId),new SqliteParameter("@ttsVersion",c.TtsServiceVersion),
            new SqliteParameter("@recoveryErrors",JsonSerializer.Serialize(EnrichmentRecoveryPolicy.Recoverable)),
            new SqliteParameter("@recoveryPolicy",EnrichmentRecoveryPolicy.ReceiptSuffix),new SqliteParameter("@demandWord",wordId ?? "")];
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
