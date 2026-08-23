using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddNormalizedWordColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "normalized_word",
                table: "vocabulary",
                type: "TEXT",
                nullable: false,
                computedColumnSql: "lower(trim(word))",
                stored: false);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_normalized_word",
                table: "vocabulary",
                column: "normalized_word");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_vocabulary_normalized_word",
                table: "vocabulary");

            migrationBuilder.DropColumn(
                name: "normalized_word",
                table: "vocabulary");
        }
    }
}
