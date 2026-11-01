using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentScheduler : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "enrichment_discovery",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    cursor = table.Column<string>(type: "TEXT", nullable: false),
                    upper_bound = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_discovery", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_service",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    configuration_version = table.Column<string>(type: "TEXT", nullable: false),
                    consecutive_failures = table.Column<int>(type: "INTEGER", nullable: false),
                    throttle_seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    blocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    pause_seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_id = table.Column<string>(type: "TEXT", nullable: true),
                    lease_expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_service", x => x.id);
                    table.CheckConstraint("CK_enrichment_service_counts", "consecutive_failures >= 0 AND throttle_seconds >= 0 AND pause_seconds >= 0 AND next_attempt_at_unix_ms >= 0");
                    table.CheckConstraint("CK_enrichment_service_lease", "(lease_id IS NULL) = (lease_expires_at_unix_ms IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "enrichment_service_probe",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    service_id = table.Column<string>(type: "TEXT", nullable: false),
                    result = table.Column<string>(type: "TEXT", nullable: false),
                    request_made = table.Column<bool>(type: "INTEGER", nullable: false),
                    started_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    finished_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_service_probe", x => x.id);
                    table.CheckConstraint("CK_enrichment_service_probe_times", "started_at_unix_ms >= 0 AND (finished_at_unix_ms IS NULL OR finished_at_unix_ms >= started_at_unix_ms)");
                    table.ForeignKey(
                        name: "FK_enrichment_service_probe_enrichment_service_service_id",
                        column: x => x.service_id,
                        principalTable: "enrichment_service",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_service_probe_service_id_started_at_unix_ms",
                table: "enrichment_service_probe",
                columns: new[] { "service_id", "started_at_unix_ms" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "enrichment_discovery");

            migrationBuilder.DropTable(
                name: "enrichment_service_probe");

            migrationBuilder.DropTable(
                name: "enrichment_service");
        }
    }
}
