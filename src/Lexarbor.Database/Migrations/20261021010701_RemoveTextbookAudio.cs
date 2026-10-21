using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTextbookAudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This trigger belongs to audio_asset, so dropping the catalog alone
            // would leave a reference to a missing table on pronunciation writes.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS textbook_audio_track_retain_publication;");
            // Retain all shared assets and object locations. This migration never
            // contacts S3; recording metadata and receipts require a backup to restore.
            migrationBuilder.DropTable(
                name: "textbook_audio_track");

            migrationBuilder.DropTable(
                name: "textbook_audio_upload_operation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-only rollback: deleted recording rows cannot be reconstructed.
            migrationBuilder.CreateTable(
                name: "textbook_audio_track",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    book_id = table.Column<string>(type: "TEXT", nullable: false),
                    current_asset_id = table.Column<string>(type: "TEXT", nullable: true),
                    display_order = table.Column<int>(type: "INTEGER", nullable: false),
                    duration_seconds = table.Column<double>(type: "REAL", nullable: true),
                    is_available = table.Column<bool>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    unit_id = table.Column<string>(type: "TEXT", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "textbook_audio_upload_operation",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "TEXT", nullable: false),
                    asset_id = table.Column<string>(type: "TEXT", nullable: false),
                    book_id = table.Column<string>(type: "TEXT", nullable: false),
                    cleanup_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    cleanup_last_error_code = table.Column<string>(type: "TEXT", nullable: true),
                    cleanup_lease_expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    cleanup_lease_fence = table.Column<long>(type: "INTEGER", nullable: false),
                    cleanup_lease_owner = table.Column<string>(type: "TEXT", nullable: true),
                    cleanup_next_attempt_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    cleanup_observed_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    cleanup_return_state = table.Column<string>(type: "TEXT", nullable: true),
                    completed_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    duration_seconds = table.Column<double>(type: "REAL", nullable: false),
                    expected_version = table.Column<string>(type: "TEXT", nullable: false),
                    receipt_asset_id = table.Column<string>(type: "TEXT", nullable: true),
                    receipt_duration_seconds = table.Column<double>(type: "REAL", nullable: true),
                    receipt_error_code = table.Column<string>(type: "TEXT", nullable: true),
                    receipt_status_code = table.Column<int>(type: "INTEGER", nullable: true),
                    receipt_track_id = table.Column<string>(type: "TEXT", nullable: true),
                    receipt_version = table.Column<string>(type: "TEXT", nullable: true),
                    request_fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    track_id = table.Column<string>(type: "TEXT", nullable: false),
                    unit_id = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    upload_lease_expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    upload_lease_fence = table.Column<long>(type: "INTEGER", nullable: false),
                    upload_lease_owner = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_textbook_audio_upload_operation", x => x.idempotency_key);
                    table.CheckConstraint("CK_textbook_audio_upload_cleanup_lease", "(state = 'CleanupClaimed' AND cleanup_lease_owner IS NOT NULL AND length(trim(cleanup_lease_owner)) > 0 AND cleanup_lease_expires_at_unix_ms IS NOT NULL AND cleanup_lease_expires_at_unix_ms > updated_at_unix_ms AND cleanup_lease_fence > 0 AND cleanup_return_state IS NOT NULL AND cleanup_return_state IN ('Abandoned','CleanupObserved')) OR (state != 'CleanupClaimed' AND cleanup_lease_owner IS NULL AND cleanup_lease_expires_at_unix_ms IS NULL AND cleanup_return_state IS NULL)");
                    table.CheckConstraint("CK_textbook_audio_upload_duration", "duration_seconds > 0 AND duration_seconds <= 1.7976931348623157e308");
                    table.CheckConstraint("CK_textbook_audio_upload_expected_version", "length(expected_version) = 36 AND substr(expected_version,9,1) = '-' AND substr(expected_version,14,1) = '-' AND substr(expected_version,19,1) = '-' AND substr(expected_version,24,1) = '-' AND length(replace(expected_version,'-','')) = 32 AND replace(expected_version,'-','') NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_textbook_audio_upload_fences", "typeof(upload_lease_fence) = 'integer' AND upload_lease_fence >= 1 AND typeof(cleanup_lease_fence) = 'integer' AND cleanup_lease_fence >= 0 AND typeof(cleanup_attempts) = 'integer' AND cleanup_attempts >= 0");
                    table.CheckConstraint("CK_textbook_audio_upload_fingerprint", "length(request_fingerprint) = 64 AND request_fingerprint NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_textbook_audio_upload_key", "length(idempotency_key) = 36 AND substr(idempotency_key,9,1) = '-' AND substr(idempotency_key,14,1) = '-' AND substr(idempotency_key,19,1) = '-' AND substr(idempotency_key,24,1) = '-' AND length(replace(idempotency_key,'-','')) = 32 AND replace(idempotency_key,'-','') NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_textbook_audio_upload_receipt", "(state IN ('Reserved', 'Transferring') AND receipt_status_code IS NULL AND receipt_asset_id IS NULL AND receipt_track_id IS NULL AND receipt_version IS NULL AND receipt_duration_seconds IS NULL AND receipt_error_code IS NULL AND completed_at_unix_ms IS NULL) OR (state = 'Succeeded' AND receipt_status_code IS NOT NULL AND receipt_status_code = 200 AND receipt_asset_id IS NOT NULL AND receipt_asset_id = asset_id AND receipt_track_id IS NOT NULL AND receipt_track_id = track_id AND receipt_version IS NOT NULL AND (length(receipt_version) = 36 AND substr(receipt_version,9,1) = '-' AND substr(receipt_version,14,1) = '-' AND substr(receipt_version,19,1) = '-' AND substr(receipt_version,24,1) = '-' AND length(replace(receipt_version,'-','')) = 32 AND replace(receipt_version,'-','') NOT GLOB '*[^0-9a-f]*') AND receipt_duration_seconds IS NOT NULL AND receipt_duration_seconds = duration_seconds AND receipt_error_code IS NULL AND completed_at_unix_ms IS NOT NULL) OR (state IN ('Abandoned', 'CleanupClaimed', 'CleanupObserved') AND receipt_status_code IS NOT NULL AND receipt_status_code IN (404,409,503) AND receipt_asset_id IS NULL AND receipt_track_id IS NULL AND receipt_version IS NULL AND receipt_duration_seconds IS NULL AND receipt_error_code IS NOT NULL AND receipt_error_code IN ('audio.upload_failed','audio.upload_expired','audio.target_not_found','audio.target_conflict','audio.storage_unavailable','audio.upload_cancelled','audio.upload_timeout') AND completed_at_unix_ms IS NOT NULL)");
                    table.CheckConstraint("CK_textbook_audio_upload_state", "state IN ('Reserved', 'Transferring', 'Succeeded', 'Abandoned', 'CleanupClaimed', 'CleanupObserved')");
                    table.CheckConstraint("CK_textbook_audio_upload_target", "length(trim(book_id)) > 0 AND book_id = trim(book_id) AND length(trim(unit_id)) > 0 AND unit_id = trim(unit_id) AND length(trim(track_id)) > 0 AND track_id = trim(track_id)");
                    table.CheckConstraint("CK_textbook_audio_upload_times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms AND (completed_at_unix_ms IS NULL OR completed_at_unix_ms >= created_at_unix_ms) AND (cleanup_next_attempt_at_unix_ms IS NULL OR cleanup_next_attempt_at_unix_ms >= 0) AND (cleanup_observed_at_unix_ms IS NULL OR cleanup_observed_at_unix_ms >= created_at_unix_ms)");
                    table.CheckConstraint("CK_textbook_audio_upload_upload_lease", "(state IN ('Reserved','Transferring') AND upload_lease_owner IS NOT NULL AND length(trim(upload_lease_owner)) > 0 AND upload_lease_expires_at_unix_ms IS NOT NULL AND upload_lease_expires_at_unix_ms > created_at_unix_ms) OR (state NOT IN ('Reserved','Transferring') AND upload_lease_owner IS NULL AND upload_lease_expires_at_unix_ms IS NULL)");
                    table.ForeignKey(
                        name: "FK_textbook_audio_upload_operation_audio_asset_asset_id",
                        column: x => x.asset_id,
                        principalTable: "audio_asset",
                        principalColumn: "id",
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

            migrationBuilder.CreateIndex(
                name: "IX_textbook_audio_upload_operation_asset_id",
                table: "textbook_audio_upload_operation",
                column: "asset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_textbook_audio_upload_operation_state_cleanup_next_attempt_at_unix_ms",
                table: "textbook_audio_upload_operation",
                columns: new[] { "state", "cleanup_next_attempt_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_textbook_audio_upload_operation_state_upload_lease_expires_at_unix_ms",
                table: "textbook_audio_upload_operation",
                columns: new[] { "state", "upload_lease_expires_at_unix_ms" });

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
    }
}
