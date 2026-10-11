using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddVocabularyAudio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite accepts the composite parent key as a unique index.
            // Avoid rebuilding vocabulary_meaning or rewriting existing rows.
            migrationBuilder.CreateTable(
                name: "audio_asset",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    bucket = table.Column<string>(type: "TEXT", nullable: false),
                    object_key = table.Column<string>(type: "TEXT", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    byte_length = table.Column<long>(type: "INTEGER", nullable: false),
                    content_type = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    first_published_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audio_asset", x => x.id);
                    table.CheckConstraint("CK_audio_asset_content_type", "content_type = 'audio/mpeg'");
                    table.CheckConstraint("CK_audio_asset_location", "length(bucket) > 0 AND length(object_key) > 0");
                    table.CheckConstraint("CK_audio_asset_sha256", "length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_audio_asset_size", "byte_length > 0");
                    table.CheckConstraint("CK_audio_asset_times", "created_at_unix_ms >= 0 AND (first_published_at_unix_ms IS NULL OR first_published_at_unix_ms >= created_at_unix_ms)");
                });

            migrationBuilder.CreateTable(
                name: "audio_upload_operation",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "TEXT", nullable: false),
                    request_fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    word_id = table.Column<string>(type: "TEXT", nullable: false),
                    meaning_id = table.Column<string>(type: "TEXT", nullable: true),
                    locale = table.Column<string>(type: "TEXT", nullable: false),
                    source_revision = table.Column<string>(type: "TEXT", nullable: false),
                    expected_asset_id = table.Column<string>(type: "TEXT", nullable: true),
                    asset_id = table.Column<string>(type: "TEXT", nullable: false),
                    producer = table.Column<string>(type: "TEXT", nullable: true),
                    voice = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    upload_lease_owner = table.Column<string>(type: "TEXT", nullable: true),
                    upload_lease_fence = table.Column<long>(type: "INTEGER", nullable: false),
                    upload_lease_expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    receipt_status_code = table.Column<int>(type: "INTEGER", nullable: true),
                    receipt_asset_id = table.Column<string>(type: "TEXT", nullable: true),
                    receipt_source_revision = table.Column<string>(type: "TEXT", nullable: true),
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
                    table.PrimaryKey("PK_audio_upload_operation", x => x.idempotency_key);
                    table.CheckConstraint("CK_audio_upload_operation_cleanup_lease", "state != 'CleanupClaimed' OR (cleanup_lease_owner IS NOT NULL AND cleanup_lease_expires_at_unix_ms IS NOT NULL AND cleanup_lease_fence > 0 AND cleanup_return_state IS NOT NULL)");
                    table.CheckConstraint("CK_audio_upload_operation_cleanup_return", "cleanup_return_state IS NULL OR cleanup_return_state IN ('Abandoned', 'CleanupObserved')");
                    table.CheckConstraint("CK_audio_upload_operation_fences", "upload_lease_fence >= 0 AND cleanup_lease_fence >= 0 AND cleanup_attempts >= 0");
                    table.CheckConstraint("CK_audio_upload_operation_fingerprint", "length(request_fingerprint) = 64 AND request_fingerprint NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_audio_upload_operation_key", "length(idempotency_key) = 36 AND substr(idempotency_key, 9, 1) = '-' AND substr(idempotency_key, 14, 1) = '-' AND substr(idempotency_key, 19, 1) = '-' AND substr(idempotency_key, 24, 1) = '-' AND length(replace(idempotency_key, '-', '')) = 32 AND replace(idempotency_key, '-', '') NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_audio_upload_operation_locale", "locale IN ('en-GB', 'en-US')");
                    table.CheckConstraint("CK_audio_upload_operation_receipt", "(state IN ('Reserved', 'Transferring') AND receipt_status_code IS NULL AND receipt_asset_id IS NULL AND receipt_source_revision IS NULL AND receipt_error_code IS NULL AND completed_at_unix_ms IS NULL) OR (state = 'Succeeded' AND receipt_status_code IS NOT NULL AND receipt_status_code = 200 AND receipt_asset_id IS NOT NULL AND receipt_source_revision IS NOT NULL AND completed_at_unix_ms IS NOT NULL AND receipt_asset_id = asset_id AND receipt_source_revision = source_revision AND receipt_error_code IS NULL) OR (state IN ('Abandoned', 'CleanupClaimed', 'CleanupObserved') AND receipt_status_code IS NOT NULL AND receipt_status_code IN (404, 409, 503) AND receipt_asset_id IS NULL AND receipt_source_revision IS NULL AND completed_at_unix_ms IS NOT NULL AND receipt_error_code IS NOT NULL)");
                    table.CheckConstraint("CK_audio_upload_operation_revision", "length(source_revision) = 64 AND source_revision NOT GLOB '*[^0-9a-f]*'");
                    table.CheckConstraint("CK_audio_upload_operation_state", "state IN ('Reserved', 'Transferring', 'Succeeded', 'Abandoned', 'CleanupClaimed', 'CleanupObserved')");
                    table.CheckConstraint("CK_audio_upload_operation_times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms");
                    table.CheckConstraint("CK_audio_upload_operation_upload_lease", "state NOT IN ('Reserved', 'Transferring') OR (upload_lease_owner IS NOT NULL AND upload_lease_expires_at_unix_ms IS NOT NULL AND upload_lease_fence > 0)");
                    table.ForeignKey(
                        name: "FK_audio_upload_operation_audio_asset_asset_id",
                        column: x => x.asset_id,
                        principalTable: "audio_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_audio_binding",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    word_id = table.Column<string>(type: "TEXT", nullable: false),
                    meaning_id = table.Column<string>(type: "TEXT", nullable: true),
                    locale = table.Column<string>(type: "TEXT", nullable: false),
                    asset_id = table.Column<string>(type: "TEXT", nullable: false),
                    source_revision = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_audio_binding", x => x.id);
                    table.CheckConstraint("CK_vocabulary_audio_binding_locale", "locale IN ('en-GB', 'en-US')");
                    table.CheckConstraint("CK_vocabulary_audio_binding_revision", "length(source_revision) = 64 AND source_revision NOT GLOB '*[^0-9a-f]*'");
                    table.ForeignKey(
                        name: "FK_vocabulary_audio_binding_audio_asset_asset_id",
                        column: x => x.asset_id,
                        principalTable: "audio_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vocabulary_audio_binding_vocabulary_meaning_meaning_id_word_id",
                        columns: x => new { x.meaning_id, x.word_id },
                        principalTable: "vocabulary_meaning",
                        principalColumns: new[] { "id", "vocabulary_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_vocabulary_audio_binding_vocabulary_word_id",
                        column: x => x.word_id,
                        principalTable: "vocabulary",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_id_vocabulary_id",
                table: "vocabulary_meaning",
                columns: new[] { "id", "vocabulary_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audio_asset_bucket_object_key",
                table: "audio_asset",
                columns: new[] { "bucket", "object_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audio_upload_operation_asset_id",
                table: "audio_upload_operation",
                column: "asset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audio_upload_operation_state_cleanup_next_attempt_at_unix_ms",
                table: "audio_upload_operation",
                columns: new[] { "state", "cleanup_next_attempt_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_audio_upload_operation_state_upload_lease_expires_at_unix_ms",
                table: "audio_upload_operation",
                columns: new[] { "state", "upload_lease_expires_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_audio_binding_asset_id",
                table: "vocabulary_audio_binding",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_audio_binding_meaning_id_locale",
                table: "vocabulary_audio_binding",
                columns: new[] { "meaning_id", "locale" },
                unique: true,
                filter: "meaning_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_audio_binding_meaning_id_word_id",
                table: "vocabulary_audio_binding",
                columns: new[] { "meaning_id", "word_id" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_audio_binding_word_id_locale",
                table: "vocabulary_audio_binding",
                columns: new[] { "word_id", "locale" },
                unique: true,
                filter: "meaning_id IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audio_upload_operation");

            migrationBuilder.DropTable(
                name: "vocabulary_audio_binding");

            migrationBuilder.DropTable(
                name: "audio_asset");

            migrationBuilder.DropIndex(
                name: "IX_vocabulary_meaning_id_vocabulary_id",
                table: "vocabulary_meaning");
        }
    }
}
