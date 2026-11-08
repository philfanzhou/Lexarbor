using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticEnrichmentRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "fields_not_required",
                table: "enrichment_round",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "force_single",
                table: "enrichment_round",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "recovery_id",
                table: "enrichment_round",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_request_batch_policy",
                columns: table => new
                {
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    configuration_version = table.Column<string>(type: "TEXT", nullable: false),
                    size = table.Column<int>(type: "INTEGER", nullable: false),
                    fast_successes = table.Column<int>(type: "INTEGER", nullable: false),
                    last_failure_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_request_batch_policy", x => new { x.kind, x.configuration_version });
                });

            migrationBuilder.CreateTable(
                name: "enrichment_recovery",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", nullable: false),
                    input_revision = table.Column<string>(type: "TEXT", nullable: false),
                    configuration_version = table.Column<string>(type: "TEXT", nullable: false),
                    source_round = table.Column<int>(type: "INTEGER", nullable: false),
                    reason = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_recovery", x => x.id);
                    table.ForeignKey(
                        name: "FK_enrichment_recovery_enrichment_target_target_id",
                        column: x => x.target_id,
                        principalTable: "enrichment_target",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_recovery_target_id_input_revision_configuration_version",
                table: "enrichment_recovery",
                columns: new[] { "target_id", "input_revision", "configuration_version" },
                unique: true);

            // Backfill only completed field work with a matching, successfully
            // saved phrase decision. No call history or failure budget is reset.
            migrationBuilder.Sql("""
                UPDATE enrichment_round AS f SET fields_not_required=1
                WHERE f.state='Succeeded' AND EXISTS (
                  SELECT 1 FROM enrichment_target ft
                  JOIN enrichment_target pt ON pt.word_id=ft.word_id AND pt.meaning_id IS NULL
                    AND pt.locale=ft.locale AND pt.step='Prepare' AND NOT pt.deleted
                  JOIN enrichment_round p ON p.target_id=pt.id AND p.number=pt.current_round
                  WHERE ft.id=f.target_id AND ft.current_round=f.number AND ft.step='Fields' AND NOT ft.deleted
                    AND p.state='Succeeded' AND p.source_revision=f.source_revision AND json_valid(p.plan_json)
                    AND json_extract(CASE WHEN json_valid(p.plan_json) THEN p.plan_json ELSE '{}' END,'$.Preparation.EntryKind')='phrase');
                UPDATE enrichment_round SET force_single=1 WHERE consumed_failures>0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_request_batch_policy");

            migrationBuilder.DropTable(
                name: "enrichment_recovery");

            // Native DROP COLUMN preserves dependent call/wait rows and triggers.
            migrationBuilder.Sql("ALTER TABLE enrichment_round DROP COLUMN fields_not_required;");
            migrationBuilder.Sql("ALTER TABLE enrichment_round DROP COLUMN force_single;");
            migrationBuilder.Sql("ALTER TABLE enrichment_round DROP COLUMN recovery_id;");
        }
    }
}
