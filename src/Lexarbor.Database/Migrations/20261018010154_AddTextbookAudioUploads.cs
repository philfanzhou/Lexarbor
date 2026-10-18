using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddTextbookAudioUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "textbook_audio_upload_operation",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "TEXT", nullable: false),
                    request_fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    book_id = table.Column<string>(type: "TEXT", nullable: false),
                    unit_id = table.Column<string>(type: "TEXT", nullable: false),
                    track_id = table.Column<string>(type: "TEXT", nullable: false),
                    expected_version = table.Column<string>(type: "TEXT", nullable: false),
                    duration_seconds = table.Column<double>(type: "REAL", nullable: false),
                    asset_id = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    upload_lease_owner = table.Column<string>(type: "TEXT", nullable: true),
                    upload_lease_fence = table.Column<long>(type: "INTEGER", nullable: false),
                    upload_lease_expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    receipt_status_code = table.Column<int>(type: "INTEGER", nullable: true),
                    receipt_asset_id = table.Column<string>(type: "TEXT", nullable: true),
                    receipt_track_id = table.Column<string>(type: "TEXT", nullable: true),
                    receipt_version = table.Column<string>(type: "TEXT", nullable: true),
                    receipt_duration_seconds = table.Column<double>(type: "REAL", nullable: true),
                    receipt_error_code = table.Column<string>(type: "TEXT", nullable: true),
                    completed_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    cleanup_lease_owner = table.Column<string>(type: "TEXT", nullable: true),
                    cleanup_lease_fence = table.Column<long>(type: "INTEGER", nullable: false),
                    cleanup_lease_expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    cleanup_return_state = table.Column<string>(type: "TEXT", nullable: true),
                    cleanup_attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    cleanup_next_attempt_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    cleanup_last_error_code = table.Column<string>(type: "TEXT", nullable: true),
                    cleanup_observed_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "textbook_audio_upload_operation");
        }
    }
}
