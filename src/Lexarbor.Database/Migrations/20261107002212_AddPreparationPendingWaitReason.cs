using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Lexarbor.Database.Migrations;

public partial class AddPreparationPendingWaitReason : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, true);
    protected override void Down(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, false);

    private static void Rebuild(MigrationBuilder migrationBuilder, bool preparationPending)
    {
        // SQLite cannot alter a CHECK constraint in place. Rebuild the two tables
        // that persist wait reasons, copying dependent call/wait history out first
        // because dropping the round table removes its foreign-key parent.
        if (!preparationPending)
            migrationBuilder.Sql("""
                UPDATE enrichment_round SET wait_reason='ConfigurationMissing' WHERE wait_reason='PreparationPending';
                UPDATE enrichment_wait SET reason='ConfigurationMissing' WHERE reason='PreparationPending';
                """);
        var roundCheck = preparationPending
            ? "wait_reason IS NULL OR wait_reason IN ('Disabled','ConfigurationMissing','PreparationPending','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')"
            : "wait_reason IS NULL OR wait_reason IN ('Disabled','ConfigurationMissing','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')";
        var waitCheck = preparationPending
            ? "reason IN ('Disabled','ConfigurationMissing','PreparationPending','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')"
            : "reason IN ('Disabled','ConfigurationMissing','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')";
        migrationBuilder.Sql($"""
            CREATE TABLE preparation_round_backup AS SELECT * FROM enrichment_round;
            CREATE TABLE preparation_call_backup AS SELECT * FROM enrichment_call;
            CREATE TABLE preparation_wait_backup AS SELECT * FROM enrichment_wait;
            DELETE FROM enrichment_call;
            DELETE FROM enrichment_wait;
            DELETE FROM enrichment_round;
            DROP TABLE enrichment_round;
            CREATE TABLE enrichment_round (
                target_id TEXT NOT NULL,
                number INTEGER NOT NULL,
                state TEXT NOT NULL,
                input_revision TEXT NOT NULL,
                source_revision TEXT NOT NULL,
                own_asset_id TEXT NULL,
                configuration_version TEXT NOT NULL,
                retry_key TEXT NULL,
                failure_budget INTEGER NOT NULL,
                request_count INTEGER NOT NULL,
                consumed_failures INTEGER NOT NULL,
                plan_json TEXT NULL,
                wait_reason TEXT NULL,
                error_code TEXT NULL,
                next_attempt_at_unix_ms INTEGER NULL,
                lease_owner TEXT NULL,
                lease_fence INTEGER NOT NULL,
                lease_expires_at_unix_ms INTEGER NULL,
                created_at_unix_ms INTEGER NOT NULL,
                updated_at_unix_ms INTEGER NOT NULL,
                CONSTRAINT "PK_enrichment_round" PRIMARY KEY (target_id, number),
                CONSTRAINT "CK_enrichment_round_config" CHECK (length(configuration_version) BETWEEN 1 AND 128),
                CONSTRAINT "CK_enrichment_round_counts" CHECK (failure_budget BETWEEN 1 AND 20 AND consumed_failures BETWEEN 0 AND failure_budget AND request_count >= consumed_failures AND lease_fence >= 0),
                CONSTRAINT "CK_enrichment_round_input" CHECK (length(input_revision) = 64 AND input_revision NOT GLOB '*[^0-9a-f]*' AND length(source_revision) = 64 AND source_revision NOT GLOB '*[^0-9a-f]*'),
                CONSTRAINT "CK_enrichment_round_lease" CHECK ((state = 'Running' AND lease_owner IS NOT NULL AND lease_fence > 0 AND lease_expires_at_unix_ms IS NOT NULL) OR (state != 'Running' AND lease_owner IS NULL AND lease_expires_at_unix_ms IS NULL)),
                CONSTRAINT "CK_enrichment_round_plan" CHECK (plan_json IS NULL OR length(plan_json) <= 16384),
                CONSTRAINT "CK_enrichment_round_state" CHECK (state IN ('Pending','Running','Waiting','Succeeded','Failed','Superseded','Cancelled')),
                CONSTRAINT "CK_enrichment_round_times" CHECK (created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms),
                CONSTRAINT "CK_enrichment_round_wait" CHECK ({roundCheck}),
                CONSTRAINT "FK_enrichment_round_enrichment_target_target_id" FOREIGN KEY (target_id) REFERENCES enrichment_target (id) ON DELETE CASCADE
            );
            CREATE INDEX "IX_enrichment_round_state_next_attempt_at_unix_ms" ON enrichment_round (state, next_attempt_at_unix_ms);
            CREATE UNIQUE INDEX "IX_enrichment_round_target_id_retry_key" ON enrichment_round (target_id, retry_key) WHERE retry_key IS NOT NULL;
            INSERT INTO enrichment_round SELECT * FROM preparation_round_backup;
            DROP TABLE preparation_round_backup;
            DROP TABLE enrichment_wait;
            CREATE TABLE enrichment_wait (
                id TEXT NOT NULL CONSTRAINT "PK_enrichment_wait" PRIMARY KEY,
                target_id TEXT NOT NULL,
                round INTEGER NOT NULL,
                reason TEXT NOT NULL,
                error_code TEXT NULL,
                recorded_at_unix_ms INTEGER NOT NULL,
                next_attempt_at_unix_ms INTEGER NOT NULL,
                CONSTRAINT "CK_enrichment_wait_reason" CHECK ({waitCheck}),
                CONSTRAINT "CK_enrichment_wait_times" CHECK (recorded_at_unix_ms >= 0 AND next_attempt_at_unix_ms >= recorded_at_unix_ms),
                CONSTRAINT "FK_enrichment_wait_enrichment_round_target_id_round" FOREIGN KEY (target_id, round) REFERENCES enrichment_round (target_id, number) ON DELETE CASCADE
            );
            CREATE INDEX "IX_enrichment_wait_target_id_round" ON enrichment_wait (target_id, round);
            INSERT INTO enrichment_wait SELECT * FROM preparation_wait_backup;
            DROP TABLE preparation_wait_backup;
            INSERT INTO enrichment_call SELECT * FROM preparation_call_backup;
            DROP TABLE preparation_call_backup;
            """);
    }
}
