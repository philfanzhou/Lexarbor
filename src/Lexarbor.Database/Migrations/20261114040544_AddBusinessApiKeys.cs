using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_api_key",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    secret_hash = table.Column<string>(type: "TEXT", nullable: false),
                    caller_id = table.Column<string>(type: "TEXT", nullable: false),
                    catalog = table.Column<bool>(type: "INTEGER", nullable: false),
                    meaning_submissions = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    revoked_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_api_key", x => x.id);
                    table.CheckConstraint("CK_business_api_key_caller", "length(caller_id) BETWEEN 1 AND 2048");
                    table.CheckConstraint("CK_business_api_key_hash", "length(secret_hash) = 64 AND secret_hash NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_business_api_key_scopes", "catalog IN (0, 1) AND meaning_submissions IN (0, 1) AND (catalog = 1 OR meaning_submissions = 1)");
                    table.CheckConstraint("CK_business_api_key_times", "created_at_unix_ms >= 0 AND (revoked_at_unix_ms IS NULL OR revoked_at_unix_ms >= created_at_unix_ms)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_api_key_secret_hash",
                table: "business_api_key",
                column: "secret_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_api_key");
        }
    }
}
