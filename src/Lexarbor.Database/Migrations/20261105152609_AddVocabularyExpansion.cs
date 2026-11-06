using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Lexarbor.Database.Migrations;

public partial class AddVocabularyExpansion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, true);
    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, false);

    private static void Rebuild(MigrationBuilder migrationBuilder, bool expand)
    {
        // Keep FK enforcement AND the migration history write in the same transaction.
        // A suppressed PRAGMA would commit the rebuild before EF records its version,
        // making a crash in that gap impossible to retry. Preserve cascading children
        // temporarily, then restore them after replacing the target table.
        if (!expand)
            migrationBuilder.Sql("""
                DELETE FROM enrichment_call WHERE target_id IN (SELECT id FROM enrichment_target WHERE step='Expand');
                DELETE FROM enrichment_wait WHERE target_id IN (SELECT id FROM enrichment_target WHERE step='Expand');
                DELETE FROM enrichment_round WHERE target_id IN (SELECT id FROM enrichment_target WHERE step='Expand');
                DELETE FROM enrichment_target WHERE step='Expand';
                DROP TABLE enrichment_generated_word;
                """);
        var locale = expand ? "TEXT NULL" : "TEXT NOT NULL";
        var localeCheck = expand
            ? "(step = 'Expand' AND locale IS NULL) OR (step != 'Expand' AND locale IS NOT NULL AND locale IN ('en-GB','en-US'))"
            : "locale IN ('en-GB','en-US')";
        var stepCheck = expand
            ? "(step = 'Expand' AND meaning_id IS NOT NULL) OR (step IN ('Prepare','Fields','Audio') AND (meaning_id IS NULL OR step != 'Fields'))"
            : "step IN ('Prepare','Fields','Audio') AND (meaning_id IS NULL OR step != 'Fields')";
        migrationBuilder.Sql($"""
            CREATE TABLE expansion_round_backup AS SELECT * FROM enrichment_round;
            CREATE TABLE expansion_call_backup AS SELECT * FROM enrichment_call;
            CREATE TABLE expansion_wait_backup AS SELECT * FROM enrichment_wait;
            DELETE FROM enrichment_call;
            DELETE FROM enrichment_wait;
            DELETE FROM enrichment_round;
            DROP TRIGGER enrichment_word_deleted;
            DROP TRIGGER enrichment_meaning_deleted;
            CREATE TABLE expansion_target_rebuild (
                id TEXT NOT NULL CONSTRAINT PK_enrichment_target PRIMARY KEY,
                word_id TEXT NOT NULL, meaning_id TEXT NULL, locale {locale}, step TEXT NOT NULL,
                current_round INTEGER NOT NULL, epoch INTEGER NOT NULL, deleted INTEGER NOT NULL,
                CONSTRAINT CK_enrichment_target_locale CHECK ({localeCheck}),
                CONSTRAINT CK_enrichment_target_step CHECK ({stepCheck}),
                CONSTRAINT CK_enrichment_target_version CHECK (current_round > 0 AND epoch > 0)
            );
            INSERT INTO expansion_target_rebuild (id,word_id,meaning_id,locale,step,current_round,epoch,deleted)
                SELECT id,word_id,meaning_id,locale,step,current_round,epoch,deleted FROM enrichment_target;
            DROP TABLE enrichment_target;
            ALTER TABLE expansion_target_rebuild RENAME TO enrichment_target;
            CREATE UNIQUE INDEX IX_enrichment_target_word_id_locale_step ON enrichment_target(word_id,locale,step) WHERE meaning_id IS NULL;
            CREATE UNIQUE INDEX IX_enrichment_target_word_id_meaning_id_locale_step ON enrichment_target(word_id,meaning_id,locale,step) WHERE meaning_id IS NOT NULL;
            INSERT INTO enrichment_round SELECT * FROM expansion_round_backup;
            INSERT INTO enrichment_call SELECT * FROM expansion_call_backup;
            INSERT INTO enrichment_wait SELECT * FROM expansion_wait_backup;
            DROP TABLE expansion_wait_backup;
            DROP TABLE expansion_call_backup;
            DROP TABLE expansion_round_backup;
            """);
        if (expand)
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IX_enrichment_target_word_id_meaning_id ON enrichment_target(word_id,meaning_id) WHERE step='Expand';
                CREATE TABLE enrichment_generated_word (
                    word_id TEXT NOT NULL CONSTRAINT PK_enrichment_generated_word PRIMARY KEY,
                    task_id TEXT NOT NULL,
                    CONSTRAINT FK_enrichment_generated_word_vocabulary_word_id FOREIGN KEY(word_id) REFERENCES vocabulary(id) ON DELETE CASCADE
                );
                """);
        migrationBuilder.Sql("""
                CREATE TRIGGER enrichment_word_deleted AFTER DELETE ON vocabulary
                BEGIN
                    UPDATE enrichment_target SET deleted = 1, epoch = epoch + 1
                    WHERE word_id = OLD.id AND deleted = 0;
                    UPDATE enrichment_round SET state = 'Cancelled', lease_owner = NULL,
                        lease_expires_at_unix_ms = NULL, updated_at_unix_ms = MAX(updated_at_unix_ms, CAST(unixepoch('subsec') * 1000 AS INTEGER))
                    WHERE target_id IN (SELECT id FROM enrichment_target WHERE word_id = OLD.id)
                        AND state IN ('Pending','Running','Waiting');
                END;
                CREATE TRIGGER enrichment_meaning_deleted AFTER DELETE ON vocabulary_meaning
                BEGIN
                    UPDATE enrichment_target SET deleted = 1, epoch = epoch + 1
                    WHERE meaning_id = OLD.id AND deleted = 0;
                    UPDATE enrichment_round SET state = 'Cancelled', lease_owner = NULL,
                        lease_expires_at_unix_ms = NULL, updated_at_unix_ms = MAX(updated_at_unix_ms, CAST(unixepoch('subsec') * 1000 AS INTEGER))
                    WHERE target_id IN (SELECT id FROM enrichment_target WHERE meaning_id = OLD.id)
                        AND state IN ('Pending','Running','Waiting');
                END;
                """);
    }
}
