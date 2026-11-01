using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "enrichment_target",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    word_id = table.Column<string>(type: "TEXT", nullable: false),
                    meaning_id = table.Column<string>(type: "TEXT", nullable: true),
                    locale = table.Column<string>(type: "TEXT", nullable: false),
                    step = table.Column<string>(type: "TEXT", nullable: false),
                    current_round = table.Column<int>(type: "INTEGER", nullable: false),
                    epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_target", x => x.id);
                    table.CheckConstraint("CK_enrichment_target_locale", "locale IN ('en-GB','en-US')");
                    table.CheckConstraint("CK_enrichment_target_step", "step IN ('Prepare','Fields','Audio') AND (meaning_id IS NULL OR step != 'Fields')");
                    table.CheckConstraint("CK_enrichment_target_version", "current_round > 0 AND epoch > 0");
                });

            migrationBuilder.CreateTable(
                name: "enrichment_round",
                columns: table => new
                {
                    target_id = table.Column<string>(type: "TEXT", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    input_revision = table.Column<string>(type: "TEXT", nullable: false),
                    source_revision = table.Column<string>(type: "TEXT", nullable: false),
                    own_asset_id = table.Column<string>(type: "TEXT", nullable: true),
                    configuration_version = table.Column<string>(type: "TEXT", nullable: false),
                    retry_key = table.Column<string>(type: "TEXT", nullable: true),
                    failure_budget = table.Column<int>(type: "INTEGER", nullable: false),
                    request_count = table.Column<int>(type: "INTEGER", nullable: false),
                    consumed_failures = table.Column<int>(type: "INTEGER", nullable: false),
                    plan_json = table.Column<string>(type: "TEXT", nullable: true),
                    wait_reason = table.Column<string>(type: "TEXT", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", nullable: true),
                    next_attempt_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    lease_owner = table.Column<string>(type: "TEXT", nullable: true),
                    lease_fence = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_round", x => new { x.target_id, x.number });
                    table.CheckConstraint("CK_enrichment_round_config", "length(configuration_version) BETWEEN 1 AND 128");
                    table.CheckConstraint("CK_enrichment_round_counts", "failure_budget BETWEEN 1 AND 20 AND consumed_failures BETWEEN 0 AND failure_budget AND request_count >= consumed_failures AND lease_fence >= 0");
                    table.CheckConstraint("CK_enrichment_round_input", "length(input_revision) = 64 AND input_revision NOT GLOB '*[^0-9a-f]*' AND length(source_revision) = 64 AND source_revision NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_enrichment_round_lease", "(state = 'Running' AND lease_owner IS NOT NULL AND lease_fence > 0 AND lease_expires_at_unix_ms IS NOT NULL) OR (state != 'Running' AND lease_owner IS NULL AND lease_expires_at_unix_ms IS NULL)");
                    table.CheckConstraint("CK_enrichment_round_plan", "plan_json IS NULL OR length(plan_json) <= 16384");
                    table.CheckConstraint("CK_enrichment_round_state", "state IN ('Pending','Running','Waiting','Succeeded','Failed','Superseded','Cancelled')");
                    table.CheckConstraint("CK_enrichment_round_times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms");
                    table.CheckConstraint("CK_enrichment_round_wait", "wait_reason IS NULL OR wait_reason IN ('Disabled','ConfigurationMissing','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')");
                    table.ForeignKey(
                        name: "FK_enrichment_round_enrichment_target_target_id",
                        column: x => x.target_id,
                        principalTable: "enrichment_target",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_call",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", nullable: false),
                    round = table.Column<int>(type: "INTEGER", nullable: false),
                    service = table.Column<string>(type: "TEXT", nullable: false),
                    result = table.Column<string>(type: "TEXT", nullable: false),
                    upload_idempotency_key = table.Column<string>(type: "TEXT", nullable: true),
                    error_code = table.Column<string>(type: "TEXT", nullable: true),
                    started_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    finished_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_call", x => x.id);
                    table.CheckConstraint("CK_enrichment_call_result", "result IN ('Started','Succeeded','Throttled','TransientFailure','TerminalFailure')");
                    table.CheckConstraint("CK_enrichment_call_service", "service IN ('text','tts')");
                    table.CheckConstraint("CK_enrichment_call_times", "started_at_unix_ms >= 0 AND ((result = 'Started' AND finished_at_unix_ms IS NULL) OR (result != 'Started' AND finished_at_unix_ms >= started_at_unix_ms))");
                    table.ForeignKey(
                        name: "FK_enrichment_call_enrichment_round_target_id_round",
                        columns: x => new { x.target_id, x.round },
                        principalTable: "enrichment_round",
                        principalColumns: new[] { "target_id", "number" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_wait",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", nullable: false),
                    round = table.Column<int>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    error_code = table.Column<string>(type: "TEXT", nullable: true),
                    recorded_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    next_attempt_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_wait", x => x.id);
                    table.CheckConstraint("CK_enrichment_wait_reason", "reason IN ('Disabled','ConfigurationMissing','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')");
                    table.CheckConstraint("CK_enrichment_wait_times", "recorded_at_unix_ms >= 0 AND next_attempt_at_unix_ms >= recorded_at_unix_ms");
                    table.ForeignKey(
                        name: "FK_enrichment_wait_enrichment_round_target_id_round",
                        columns: x => new { x.target_id, x.round },
                        principalTable: "enrichment_round",
                        principalColumns: new[] { "target_id", "number" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_call_target_id_round",
                table: "enrichment_call",
                columns: new[] { "target_id", "round" },
                unique: true,
                filter: "result = 'Started'");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_call_upload_idempotency_key",
                table: "enrichment_call",
                column: "upload_idempotency_key",
                unique: true,
                filter: "upload_idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_round_state_next_attempt_at_unix_ms",
                table: "enrichment_round",
                columns: new[] { "state", "next_attempt_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_round_target_id_retry_key",
                table: "enrichment_round",
                columns: new[] { "target_id", "retry_key" },
                unique: true,
                filter: "retry_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_target_word_id_locale_step",
                table: "enrichment_target",
                columns: new[] { "word_id", "locale", "step" },
                unique: true,
                filter: "meaning_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_target_word_id_meaning_id_locale_step",
                table: "enrichment_target",
                columns: new[] { "word_id", "meaning_id", "locale", "step" },
                unique: true,
                filter: "meaning_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_wait_target_id_round",
                table: "enrichment_wait",
                columns: new[] { "target_id", "round" });
            // Preserve task/call history while revoking work in every deletion
            // path, including cascading book/meaning cleanup and direct SQL.
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER enrichment_word_deleted; DROP TRIGGER enrichment_meaning_deleted;");
            migrationBuilder.DropTable(
                name: "enrichment_call");

            migrationBuilder.DropTable(
                name: "enrichment_wait");

            migrationBuilder.DropTable(
                name: "enrichment_round");

            migrationBuilder.DropTable(
                name: "enrichment_target");
        }
    }
}
