using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMeaningSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meaning_submission",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    source = table.Column<string>(type: "TEXT", nullable: false),
                    word = table.Column<string>(type: "TEXT", nullable: false),
                    normalized_word = table.Column<string>(type: "TEXT", nullable: false, computedColumnSql: "lower(trim(word))", stored: true),
                    part_of_speech = table.Column<string>(type: "TEXT", nullable: true),
                    normalized_part_of_speech = table.Column<string>(type: "TEXT", nullable: false, computedColumnSql: "lower(trim(coalesce(part_of_speech, '')))", stored: true),
                    meaning = table.Column<string>(type: "TEXT", nullable: false),
                    normalized_meaning = table.Column<string>(type: "TEXT", nullable: false, computedColumnSql: "trim(meaning)", stored: true),
                    example = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    result_book_id = table.Column<string>(type: "TEXT", nullable: true),
                    result_word_id = table.Column<string>(type: "TEXT", nullable: true),
                    result_meaning_id = table.Column<string>(type: "TEXT", nullable: true),
                    result_word = table.Column<string>(type: "TEXT", nullable: true),
                    result_meaning = table.Column<string>(type: "TEXT", nullable: true),
                    result_part_of_speech = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    reviewed_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_submission", x => x.id);
                    table.CheckConstraint("CK_meaning_submission_status", "status IN (0, 1, 2)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_key",
                table: "meaning_submission",
                columns: new[] { "normalized_word", "normalized_part_of_speech", "normalized_meaning" });

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_pending_key",
                table: "meaning_submission",
                columns: new[] { "normalized_word", "normalized_part_of_speech", "normalized_meaning" },
                unique: true,
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_status_created_at_unix_ms",
                table: "meaning_submission",
                columns: new[] { "status", "created_at_unix_ms" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meaning_submission");
        }
    }
}
