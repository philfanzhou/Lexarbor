using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentTakeover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "initial_completed",
                table: "enrichment_discovery",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "initial_scanned",
                table: "enrichment_discovery",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "initial_total",
                table: "enrichment_discovery",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_call_result_finished_at_unix_ms", table: "enrichment_call",
                columns: new[] { "result", "finished_at_unix_ms" });
            migrationBuilder.CreateIndex(
                name: "IX_enrichment_call_service_result_finished_at_unix_ms", table: "enrichment_call",
                columns: new[] { "service", "result", "finished_at_unix_ms" });

            // Continue an interrupted pre-upgrade sweep with honest progress counters.
            migrationBuilder.Sql("""
                UPDATE enrichment_discovery SET
                  initial_scanned=CASE id WHEN 'words' THEN (SELECT count(*) FROM vocabulary WHERE id<=cursor)
                    WHEN 'meanings' THEN (SELECT count(*) FROM vocabulary_meaning WHERE id<=cursor) ELSE 0 END,
                  initial_total=CASE id WHEN 'words' THEN (SELECT count(*) FROM vocabulary WHERE upper_bound='' OR id<=upper_bound)
                    WHEN 'meanings' THEN (SELECT count(*) FROM vocabulary_meaning WHERE upper_bound='' OR id<=upper_bound) ELSE 0 END;
                """);

            migrationBuilder.CreateTable(
                name: "enrichment_destination",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    book_id = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_destination", x => x.id);
                    table.ForeignKey(
                        name: "FK_enrichment_destination_vocabulary_book_book_id",
                        column: x => x.book_id,
                        principalTable: "vocabulary_book",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_destination_book_id",
                table: "enrichment_destination",
                column: "book_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_enrichment_call_result_finished_at_unix_ms", table: "enrichment_call");
            migrationBuilder.DropIndex(name: "IX_enrichment_call_service_result_finished_at_unix_ms", table: "enrichment_call");

            migrationBuilder.DropTable(
                name: "enrichment_destination");

            migrationBuilder.DropColumn(
                name: "initial_completed",
                table: "enrichment_discovery");

            migrationBuilder.DropColumn(
                name: "initial_scanned",
                table: "enrichment_discovery");

            migrationBuilder.DropColumn(
                name: "initial_total",
                table: "enrichment_discovery");
        }
    }
}
