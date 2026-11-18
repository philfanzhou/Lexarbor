using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacySqliteImportMarker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "legacy_sqlite_import",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source_schema_version = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    copied_tables = table.Column<int>(type: "integer", nullable: false),
                    copied_rows = table.Column<long>(type: "bigint", nullable: false),
                    completed_at_unix_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legacy_sqlite_import", x => x.id);
                    table.CheckConstraint("CK_legacy_sqlite_import_counts", "copied_tables >= 0 AND copied_rows >= 0");
                    table.CheckConstraint("CK_legacy_sqlite_import_outcome", "outcome IN ('imported', 'none')");
                    table.CheckConstraint("CK_legacy_sqlite_import_singleton", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "legacy_sqlite_import");
        }
    }
}
