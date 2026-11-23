using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <summary>
    /// Indexes provider calls by target in start order. Loading a task, the administration task detail
    /// and the audio upload confirmation read a target's calls; the partial unique (target_id, round)
    /// index covers only calls that are still Started, so those reads scanned the whole table.
    /// </summary>
    public partial class IndexEnrichmentCallsByTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_enrichment_call_target_id_started_at_unix_ms",
                table: "enrichment_call",
                columns: new[] { "target_id", "started_at_unix_ms" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_enrichment_call_target_id_started_at_unix_ms",
                table: "enrichment_call");
        }
    }
}
