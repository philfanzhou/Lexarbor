using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AiRequestBatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_request_batch",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    configuration_version = table.Column<string>(type: "TEXT", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", nullable: false),
                    next_size = table.Column<int>(type: "INTEGER", nullable: false),
                    started_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    finished_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_request_batch", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_service_lease",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    service_id = table.Column<string>(type: "TEXT", nullable: false),
                    configuration_version = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_service_lease", x => x.id);
                    table.ForeignKey(
                        name: "FK_enrichment_service_lease_enrichment_service_service_id",
                        column: x => x.service_id,
                        principalTable: "enrichment_service",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_request_batch_member",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    batch_id = table.Column<string>(type: "TEXT", nullable: false),
                    target_id = table.Column<string>(type: "TEXT", nullable: false),
                    outcome = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_request_batch_member", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_request_batch_member_ai_request_batch_batch_id",
                        column: x => x.batch_id,
                        principalTable: "ai_request_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_batch_kind_configuration_version_finished_at_unix_ms",
                table: "ai_request_batch",
                columns: new[] { "kind", "configuration_version", "finished_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_batch_outcome",
                table: "ai_request_batch",
                column: "outcome");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_batch_member_batch_id",
                table: "ai_request_batch_member",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_batch_member_target_id",
                table: "ai_request_batch_member",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_service_lease_service_id_configuration_version_expires_at_unix_ms",
                table: "enrichment_service_lease",
                columns: new[] { "service_id", "configuration_version", "expires_at_unix_ms" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_request_batch_member");

            migrationBuilder.DropTable(
                name: "enrichment_service_lease");

            migrationBuilder.DropTable(
                name: "ai_request_batch");
        }
    }
}
