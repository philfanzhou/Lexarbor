using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddTextbookAudioTracks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "textbook_audio_track",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    book_id = table.Column<string>(type: "TEXT", nullable: false),
                    unit_id = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    display_order = table.Column<int>(type: "INTEGER", nullable: false),
                    current_asset_id = table.Column<string>(type: "TEXT", nullable: true),
                    duration_seconds = table.Column<double>(type: "REAL", nullable: true),
                    is_available = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_textbook_audio_track", x => x.id);
                    table.CheckConstraint("CK_textbook_audio_track_available", "is_available IN (0, 1)");
                    table.CheckConstraint("CK_textbook_audio_track_file", "(current_asset_id IS NULL AND duration_seconds IS NULL AND is_available = 0) OR (current_asset_id IS NOT NULL AND duration_seconds IS NOT NULL AND duration_seconds > 0 AND duration_seconds <= 1.7976931348623157e308)");
                    table.CheckConstraint("CK_textbook_audio_track_order", "display_order >= 0 AND display_order <= 2147483647 AND typeof(display_order) = 'integer'");
                    table.CheckConstraint("CK_textbook_audio_track_title", "length(trim(title)) > 0");
                    table.CheckConstraint("CK_textbook_audio_track_version", "length(version) = 36 AND substr(version,9,1) = '-' AND substr(version,14,1) = '-' AND substr(version,19,1) = '-' AND substr(version,24,1) = '-' AND length(replace(version,'-','')) = 32 AND replace(version,'-','') NOT GLOB '*[^0-9a-f]*'");
                    table.ForeignKey(
                        name: "FK_textbook_audio_track_audio_asset_current_asset_id",
                        column: x => x.current_asset_id,
                        principalTable: "audio_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_textbook_audio_track_vocabulary_book_book_id",
                        column: x => x.book_id,
                        principalTable: "vocabulary_book",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_textbook_audio_track_vocabulary_book_unit_unit_id_book_id",
                        columns: x => new { x.unit_id, x.book_id },
                        principalTable: "vocabulary_book_unit",
                        principalColumns: new[] { "id", "book_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_textbook_audio_track_book_id",
                table: "textbook_audio_track",
                column: "book_id");

            migrationBuilder.CreateIndex(
                name: "IX_textbook_audio_track_current_asset_id",
                table: "textbook_audio_track",
                column: "current_asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_textbook_audio_track_unit_id_book_id",
                table: "textbook_audio_track",
                columns: new[] { "unit_id", "book_id" });

            migrationBuilder.CreateIndex(
                name: "IX_textbook_audio_track_unit_id_display_order_id",
                table: "textbook_audio_track",
                columns: new[] { "unit_id", "display_order", "id" });
            // SQLite CHECK expressions cannot inspect another table. These
            // triggers preserve publication as a cross-table availability rule.
            migrationBuilder.Sql("""
                CREATE TRIGGER textbook_audio_track_published_insert
                BEFORE INSERT ON textbook_audio_track
                WHEN NEW.is_available = 1 AND NOT EXISTS (
                    SELECT 1 FROM audio_asset WHERE id=NEW.current_asset_id AND first_published_at_unix_ms IS NOT NULL)
                BEGIN SELECT RAISE(ABORT, 'Available textbook audio requires a published asset.'); END;
                CREATE TRIGGER textbook_audio_track_published_update
                BEFORE UPDATE OF is_available,current_asset_id ON textbook_audio_track
                WHEN NEW.is_available = 1 AND NOT EXISTS (
                    SELECT 1 FROM audio_asset WHERE id=NEW.current_asset_id AND first_published_at_unix_ms IS NOT NULL)
                BEGIN SELECT RAISE(ABORT, 'Available textbook audio requires a published asset.'); END;
                CREATE TRIGGER textbook_audio_track_retain_publication
                BEFORE UPDATE OF first_published_at_unix_ms ON audio_asset
                WHEN NEW.first_published_at_unix_ms IS NULL AND EXISTS (
                    SELECT 1 FROM textbook_audio_track WHERE current_asset_id=NEW.id AND is_available=1)
                BEGIN SELECT RAISE(ABORT, 'An available textbook audio track requires retained publication.'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS textbook_audio_track_retain_publication;");
            migrationBuilder.DropTable(
                name: "textbook_audio_track");
        }
    }
}
