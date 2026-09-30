using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_session",
                columns: table => new
                {
                    handle_hash = table.Column<string>(type: "TEXT", nullable: false),
                    expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    protected_payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_session", x => x.handle_hash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_session_expires_at_unix_ms_handle_hash",
                table: "admin_session",
                columns: new[] { "expires_at_unix_ms", "handle_hash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_session");
        }
    }
}
