using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddLearningScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_business_api_key_scopes",
                table: "business_api_key");

            migrationBuilder.AddColumn<bool>(
                name: "learning",
                table: "business_api_key",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_business_api_key_scopes",
                table: "business_api_key",
                sql: "catalog OR meaning_submissions OR learning");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_business_api_key_scopes",
                table: "business_api_key");

            migrationBuilder.DropColumn(
                name: "learning",
                table: "business_api_key");

            migrationBuilder.AddCheckConstraint(
                name: "CK_business_api_key_scopes",
                table: "business_api_key",
                sql: "catalog OR meaning_submissions");
        }
    }
}
