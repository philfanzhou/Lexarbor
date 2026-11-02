using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddMeaningSubmissionReviewChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meaning_submission_review_check",
                columns: table => new
                {
                    record_id = table.Column<string>(type: "TEXT", nullable: false),
                    checked_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_submission_review_check", x => x.record_id);
                    table.ForeignKey(
                        name: "FK_meaning_submission_review_check_meaning_submission_ai_review_record_id",
                        column: x => x.record_id,
                        principalTable: "meaning_submission_ai_review",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meaning_submission_review_check");
        }
    }
}
