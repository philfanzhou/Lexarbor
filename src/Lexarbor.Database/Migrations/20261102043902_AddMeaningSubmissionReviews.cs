using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMeaningSubmissionReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ai_review_verdict",
                table: "meaning_submission",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_comment",
                table: "meaning_submission",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reviewer_kind",
                table: "meaning_submission",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "meaning_submission_ai_review",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    attempt_id = table.Column<string>(type: "TEXT", nullable: false),
                    submission_id = table.Column<string>(type: "TEXT", nullable: false),
                    verdict = table.Column<string>(type: "TEXT", nullable: false),
                    part_of_speech = table.Column<string>(type: "TEXT", nullable: true),
                    meaning = table.Column<string>(type: "TEXT", nullable: true),
                    phonetic_uk = table.Column<string>(type: "TEXT", nullable: true),
                    phonetic_us = table.Column<string>(type: "TEXT", nullable: true),
                    comment = table.Column<string>(type: "TEXT", nullable: true),
                    model = table.Column<string>(type: "TEXT", nullable: false),
                    reviewed_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    applied = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_submission_ai_review", x => x.id);
                    table.CheckConstraint("CK_meaning_submission_ai_review_verdict", "verdict IN ('accept', 'revise', 'reject')");
                    table.ForeignKey(
                        name: "FK_meaning_submission_ai_review_meaning_submission_submission_id",
                        column: x => x.submission_id,
                        principalTable: "meaning_submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                CREATE TRIGGER meaning_submission_ai_review_no_update
                BEFORE UPDATE ON meaning_submission_ai_review
                BEGIN SELECT RAISE(ABORT, 'AI review conclusions are immutable.'); END;
                CREATE TRIGGER meaning_submission_ai_review_no_delete
                BEFORE DELETE ON meaning_submission_ai_review
                BEGIN SELECT RAISE(ABORT, 'AI review conclusions are immutable.'); END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_ai_review_attempt_id",
                table: "meaning_submission_ai_review",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_ai_review_submission_id_reviewed_at_unix_ms",
                table: "meaning_submission_ai_review",
                columns: new[] { "submission_id", "reviewed_at_unix_ms" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meaning_submission_ai_review");

            migrationBuilder.DropColumn(
                name: "ai_review_verdict",
                table: "meaning_submission");

            migrationBuilder.DropColumn(
                name: "review_comment",
                table: "meaning_submission");

            migrationBuilder.DropColumn(
                name: "reviewer_kind",
                table: "meaning_submission");
        }
    }
}
