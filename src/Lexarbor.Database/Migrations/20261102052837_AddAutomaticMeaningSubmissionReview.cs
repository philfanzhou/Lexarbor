using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticMeaningSubmissionReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meaning_submission_auto_review",
                columns: table => new
                {
                    submission_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    attempt_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    owner = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    epoch = table.Column<long>(type: "INTEGER", nullable: false),
                    lease_until_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    next_attempt_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    consecutive_failures = table.Column<int>(type: "INTEGER", nullable: false),
                    last_failure_code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    last_failure_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    validated_review_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_submission_auto_review", x => x.submission_id);
                    table.ForeignKey(
                        name: "FK_meaning_submission_auto_review_meaning_submission_submission_id",
                        column: x => x.submission_id,
                        principalTable: "meaning_submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_auto_review_next_attempt_at_unix_ms_lease_until_unix_ms",
                table: "meaning_submission_auto_review",
                columns: new[] { "next_attempt_at_unix_ms", "lease_until_unix_ms" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meaning_submission_auto_review");
        }
    }
}
