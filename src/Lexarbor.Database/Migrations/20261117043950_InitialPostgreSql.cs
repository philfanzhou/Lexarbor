using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_session",
                columns: table => new
                {
                    handle_hash = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    expires_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    protected_payload = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_session", x => x.handle_hash);
                });

            migrationBuilder.CreateTable(
                name: "ai_request_batch",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    kind = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    configuration_version = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    outcome = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    next_size = table.Column<int>(type: "integer", nullable: false),
                    started_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    finished_at_unix_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_request_batch", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_request_batch_policy",
                columns: table => new
                {
                    kind = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    configuration_version = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    size = table.Column<int>(type: "integer", nullable: false),
                    fast_successes = table.Column<int>(type: "integer", nullable: false),
                    last_failure_at_unix_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_request_batch_policy", x => new { x.kind, x.configuration_version });
                });

            migrationBuilder.CreateTable(
                name: "audio_asset",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    bucket = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    object_key = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    sha256 = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    byte_length = table.Column<long>(type: "bigint", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    created_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    first_published_at_unix_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audio_asset", x => x.id);
                    table.CheckConstraint("CK_audio_asset_content_type", "content_type = 'audio/mpeg'");
                    table.CheckConstraint("CK_audio_asset_location", "length(bucket) > 0 AND length(object_key) > 0");
                    table.CheckConstraint("CK_audio_asset_sha256", "length(sha256) = 64 AND sha256 !~ '[^0-9a-f]'");
                    table.CheckConstraint("CK_audio_asset_size", "byte_length > 0");
                    table.CheckConstraint("CK_audio_asset_times", "created_at_unix_ms >= 0 AND (first_published_at_unix_ms IS NULL OR first_published_at_unix_ms >= created_at_unix_ms)");
                });

            migrationBuilder.CreateTable(
                name: "business_api_key",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    secret_hash = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    caller_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    catalog = table.Column<bool>(type: "boolean", nullable: false),
                    meaning_submissions = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    revoked_at_unix_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_api_key", x => x.id);
                    table.CheckConstraint("CK_business_api_key_caller", "length(caller_id) BETWEEN 1 AND 2048");
                    table.CheckConstraint("CK_business_api_key_hash", "length(secret_hash) = 64 AND secret_hash !~ '[^0-9a-f]'");
                    table.CheckConstraint("CK_business_api_key_scopes", "catalog OR meaning_submissions");
                    table.CheckConstraint("CK_business_api_key_times", "created_at_unix_ms >= 0 AND (revoked_at_unix_ms IS NULL OR revoked_at_unix_ms >= created_at_unix_ms)");
                });

            migrationBuilder.CreateTable(
                name: "enrichment_discovery",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    cursor = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    upper_bound = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    initial_completed = table.Column<bool>(type: "boolean", nullable: false),
                    initial_scanned = table.Column<int>(type: "integer", nullable: false),
                    initial_total = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_discovery", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_service",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    configuration_version = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    throttle_seconds = table.Column<int>(type: "integer", nullable: false),
                    paused = table.Column<bool>(type: "boolean", nullable: false),
                    blocked = table.Column<bool>(type: "boolean", nullable: false),
                    pause_seconds = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    lease_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    lease_expires_at_unix_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_service", x => x.id);
                    table.CheckConstraint("CK_enrichment_service_counts", "consecutive_failures >= 0 AND throttle_seconds >= 0 AND pause_seconds >= 0 AND next_attempt_at_unix_ms >= 0");
                    table.CheckConstraint("CK_enrichment_service_lease", "(lease_id IS NULL) = (lease_expires_at_unix_ms IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "enrichment_target",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    word_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    meaning_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    locale = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    step = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    current_round = table.Column<int>(type: "integer", nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_target", x => x.id);
                    table.CheckConstraint("CK_enrichment_target_locale", "(step = 'Expand' AND locale IS NULL) OR (step != 'Expand' AND locale IS NOT NULL AND locale IN ('en-GB','en-US'))");
                    table.CheckConstraint("CK_enrichment_target_step", "(step = 'Expand' AND meaning_id IS NOT NULL) OR (step IN ('Prepare','Fields','Audio') AND (meaning_id IS NULL OR step != 'Fields'))");
                    table.CheckConstraint("CK_enrichment_target_version", "current_round > 0 AND epoch > 0");
                });

            migrationBuilder.CreateTable(
                name: "meaning_submission",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    word = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    normalized_word = table.Column<string>(type: "text", nullable: false, computedColumnSql: "translate(trim(word), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')", stored: true, collation: "C"),
                    part_of_speech = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    normalized_part_of_speech = table.Column<string>(type: "text", nullable: false, computedColumnSql: "translate(trim(coalesce(part_of_speech, '')), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')", stored: true, collation: "C"),
                    meaning = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    normalized_meaning = table.Column<string>(type: "text", nullable: false, computedColumnSql: "trim(meaning)", stored: true, collation: "C"),
                    example = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    status = table.Column<int>(type: "integer", nullable: false),
                    result_book_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    result_word_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    result_meaning_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    result_word = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    result_meaning = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    result_part_of_speech = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    review_comment = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    reviewer_kind = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    ai_review_verdict = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    created_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    reviewed_at_unix_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_submission", x => x.id);
                    table.CheckConstraint("CK_meaning_submission_status", "status IN (0, 1, 2)");
                });

            migrationBuilder.CreateTable(
                name: "service_audit_logs",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false, collation: "C"),
                    operator_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true, collation: "C"),
                    operator_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true, collation: "C"),
                    operator_source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    action = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    target_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, collation: "C"),
                    target_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    client_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, collation: "C"),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true, collation: "C"),
                    security_description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true, collation: "C"),
                    metadata_json = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_audit_logs", x => x.id);
                    table.CheckConstraint("ck_service_audit_logs_action_length", "action IS NULL OR octet_length(action) <= 800");
                    table.CheckConstraint("ck_service_audit_logs_client_ip_length", "client_ip IS NULL OR octet_length(client_ip) <= 256");
                    table.CheckConstraint("ck_service_audit_logs_correlation_id_length", "correlation_id IS NULL OR octet_length(correlation_id) <= 512");
                    table.CheckConstraint("ck_service_audit_logs_id_length", "id IS NULL OR octet_length(id) <= 144");
                    table.CheckConstraint("ck_service_audit_logs_id_not_empty", "id <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("ck_service_audit_logs_metadata_json_length", "metadata_json IS NULL OR octet_length(metadata_json) <= 262144");
                    table.CheckConstraint("ck_service_audit_logs_operator_display_name_length", "operator_display_name IS NULL OR octet_length(operator_display_name) <= 1024");
                    table.CheckConstraint("ck_service_audit_logs_operator_id_length", "operator_id IS NULL OR octet_length(operator_id) <= 1024");
                    table.CheckConstraint("ck_service_audit_logs_operator_source_length", "operator_source IS NULL OR octet_length(operator_source) <= 400");
                    table.CheckConstraint("ck_service_audit_logs_security_description_length", "security_description IS NULL OR octet_length(security_description) <= 16000");
                    table.CheckConstraint("ck_service_audit_logs_target_id_length", "target_id IS NULL OR octet_length(target_id) <= 1024");
                    table.CheckConstraint("ck_service_audit_logs_target_type_length", "target_type IS NULL OR octet_length(target_type) <= 800");
                });

            migrationBuilder.CreateTable(
                name: "service_data_protection_keys",
                columns: table => new
                {
                    service_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    encrypted_xml = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_data_protection_keys", x => new { x.service_id, x.key_id });
                });

            migrationBuilder.CreateTable(
                name: "vocabulary",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    word = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    normalized_word = table.Column<string>(type: "text", nullable: false, computedColumnSql: "translate(trim(word), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')", stored: true, collation: "C"),
                    phonetic_uk = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    phonetic_us = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    insertion_order = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_book",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    book_name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    description = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    publisher = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    education_level = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    grade = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    category = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<bool>(type: "boolean", nullable: false),
                    icon_url = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_book", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_request_batch_member",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    batch_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    target_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    outcome = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_request_batch_member", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_request_batch_member_ai_request_batch_batch_id",
                        column: x => x.batch_id,
                        principalTable: "ai_request_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audio_invalidation",
                columns: table => new
                {
                    asset_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    bucket = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    object_key = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    word_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    meaning_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    locale = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    authorization_epoch = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    state = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    created_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    lease_owner = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    lease_fence = table.Column<long>(type: "bigint", nullable: false),
                    lease_expires_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    delete_started_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    observed_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audio_invalidation", x => x.asset_id);
                    table.CheckConstraint("CK_audio_invalidation_authority", "length(word_id) > 0 AND length(meaning_id) > 0 AND authorization_epoch > 0 AND reason = 'specific_phonetic_changed' AND locale IN ('en-GB','en-US')");
                    table.CheckConstraint("CK_audio_invalidation_lease", "(state = 'Claimed' AND lease_owner IS NOT NULL AND lease_expires_at_unix_ms IS NOT NULL AND lease_fence > 0 AND delete_started_at_unix_ms IS NOT NULL) OR (state != 'Claimed' AND lease_owner IS NULL AND lease_expires_at_unix_ms IS NULL)");
                    table.CheckConstraint("CK_audio_invalidation_location", "length(bucket) > 0 AND length(object_key) > 0");
                    table.CheckConstraint("CK_audio_invalidation_observed", "state != 'Observed' OR (observed_at_unix_ms IS NOT NULL AND delete_started_at_unix_ms IS NOT NULL)");
                    table.CheckConstraint("CK_audio_invalidation_state", "state IN ('Waiting','Claimed','Observed') AND attempts >= 0 AND lease_fence >= 0");
                    table.CheckConstraint("CK_audio_invalidation_times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms AND next_attempt_at_unix_ms >= 0 AND (delete_started_at_unix_ms IS NULL OR delete_started_at_unix_ms >= created_at_unix_ms) AND (observed_at_unix_ms IS NULL OR observed_at_unix_ms >= delete_started_at_unix_ms)");
                    table.ForeignKey(
                        name: "FK_audio_invalidation_audio_asset_asset_id",
                        column: x => x.asset_id,
                        principalTable: "audio_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audio_upload_operation",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    request_fingerprint = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    word_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    meaning_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    locale = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source_revision = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    meaning_phonetic_epoch = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    expected_asset_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    asset_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    producer = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    voice = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    state = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    created_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    upload_lease_owner = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    upload_lease_fence = table.Column<long>(type: "bigint", nullable: false),
                    upload_lease_expires_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    receipt_status_code = table.Column<int>(type: "integer", nullable: true),
                    receipt_asset_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    receipt_source_revision = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    receipt_error_code = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    completed_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    cleanup_lease_owner = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    cleanup_lease_fence = table.Column<long>(type: "bigint", nullable: false),
                    cleanup_lease_expires_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    cleanup_return_state = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    cleanup_attempts = table.Column<int>(type: "integer", nullable: false),
                    cleanup_next_attempt_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    cleanup_last_error_code = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    cleanup_observed_at_unix_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audio_upload_operation", x => x.idempotency_key);
                    table.CheckConstraint("CK_audio_upload_operation_cleanup_lease", "state != 'CleanupClaimed' OR (cleanup_lease_owner IS NOT NULL AND cleanup_lease_expires_at_unix_ms IS NOT NULL AND cleanup_lease_fence > 0 AND cleanup_return_state IS NOT NULL)");
                    table.CheckConstraint("CK_audio_upload_operation_cleanup_return", "cleanup_return_state IS NULL OR cleanup_return_state IN ('Abandoned', 'CleanupObserved')");
                    table.CheckConstraint("CK_audio_upload_operation_fences", "upload_lease_fence >= 0 AND cleanup_lease_fence >= 0 AND cleanup_attempts >= 0");
                    table.CheckConstraint("CK_audio_upload_operation_fingerprint", "length(request_fingerprint) = 64 AND request_fingerprint !~ '[^0-9a-f]'");
                    table.CheckConstraint("CK_audio_upload_operation_key", "length(idempotency_key) = 36 AND substr(idempotency_key, 9, 1) = '-' AND substr(idempotency_key, 14, 1) = '-' AND substr(idempotency_key, 19, 1) = '-' AND substr(idempotency_key, 24, 1) = '-' AND length(replace(idempotency_key, '-', '')) = 32 AND replace(idempotency_key, '-', '') !~ '[^0-9a-f]'");
                    table.CheckConstraint("CK_audio_upload_operation_locale", "locale IN ('en-GB', 'en-US')");
                    table.CheckConstraint("CK_audio_upload_operation_receipt", "(state IN ('Reserved', 'Transferring') AND receipt_status_code IS NULL AND receipt_asset_id IS NULL AND receipt_source_revision IS NULL AND receipt_error_code IS NULL AND completed_at_unix_ms IS NULL) OR (state = 'Succeeded' AND receipt_status_code IS NOT NULL AND receipt_status_code = 200 AND receipt_asset_id IS NOT NULL AND receipt_source_revision IS NOT NULL AND completed_at_unix_ms IS NOT NULL AND receipt_asset_id = asset_id AND receipt_source_revision = source_revision AND receipt_error_code IS NULL) OR (state IN ('Abandoned', 'CleanupClaimed', 'CleanupObserved') AND receipt_status_code IS NOT NULL AND receipt_status_code IN (404, 409, 503) AND receipt_asset_id IS NULL AND receipt_source_revision IS NULL AND completed_at_unix_ms IS NOT NULL AND receipt_error_code IS NOT NULL)");
                    table.CheckConstraint("CK_audio_upload_operation_revision", "length(source_revision) = 64 AND source_revision !~ '[^0-9a-f]'");
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
                name: "enrichment_service_lease",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    service_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    configuration_version = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    expires_at_unix_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_service_lease", x => x.id);
                    table.ForeignKey(
                        name: "FK_enrichment_service_lease_enrichment_service_service_id",
                        column: x => x.service_id,
                        principalTable: "enrichment_service",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_service_probe",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    service_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    result = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    request_made = table.Column<bool>(type: "boolean", nullable: false),
                    started_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    finished_at_unix_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_service_probe", x => x.id);
                    table.CheckConstraint("CK_enrichment_service_probe_times", "started_at_unix_ms >= 0 AND (finished_at_unix_ms IS NULL OR finished_at_unix_ms >= started_at_unix_ms)");
                    table.ForeignKey(
                        name: "FK_enrichment_service_probe_enrichment_service_service_id",
                        column: x => x.service_id,
                        principalTable: "enrichment_service",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_recovery",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    target_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    input_revision = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    configuration_version = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source_round = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    created_at_unix_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_recovery", x => x.id);
                    table.ForeignKey(
                        name: "FK_enrichment_recovery_enrichment_target_target_id",
                        column: x => x.target_id,
                        principalTable: "enrichment_target",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_round",
                columns: table => new
                {
                    target_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    input_revision = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source_revision = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    own_asset_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    configuration_version = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    retry_key = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    recovery_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    force_single = table.Column<bool>(type: "boolean", nullable: false),
                    fields_not_required = table.Column<bool>(type: "boolean", nullable: false),
                    failure_budget = table.Column<int>(type: "integer", nullable: false),
                    request_count = table.Column<int>(type: "integer", nullable: false),
                    consumed_failures = table.Column<int>(type: "integer", nullable: false),
                    plan_json = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    wait_reason = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    error_code = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    next_attempt_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    lease_owner = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    lease_fence = table.Column<long>(type: "bigint", nullable: false),
                    lease_expires_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    created_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_round", x => new { x.target_id, x.number });
                    table.CheckConstraint("CK_enrichment_round_config", "length(configuration_version) BETWEEN 1 AND 128");
                    table.CheckConstraint("CK_enrichment_round_counts", "failure_budget BETWEEN 1 AND 20 AND consumed_failures BETWEEN 0 AND failure_budget AND request_count >= consumed_failures AND lease_fence >= 0");
                    table.CheckConstraint("CK_enrichment_round_input", "length(input_revision) = 64 AND input_revision !~ '[^0-9a-f]' AND length(source_revision) = 64 AND source_revision !~ '[^0-9a-f]'");
                    table.CheckConstraint("CK_enrichment_round_lease", "(state = 'Running' AND lease_owner IS NOT NULL AND lease_fence > 0 AND lease_expires_at_unix_ms IS NOT NULL) OR (state != 'Running' AND lease_owner IS NULL AND lease_expires_at_unix_ms IS NULL)");
                    table.CheckConstraint("CK_enrichment_round_plan", "plan_json IS NULL OR length(plan_json) <= 16384");
                    table.CheckConstraint("CK_enrichment_round_state", "state IN ('Pending','Running','Waiting','Succeeded','Failed','Superseded','Cancelled')");
                    table.CheckConstraint("CK_enrichment_round_times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms");
                    table.CheckConstraint("CK_enrichment_round_wait", "wait_reason IS NULL OR wait_reason IN ('Disabled','ConfigurationMissing','PreparationPending','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')");
                    table.ForeignKey(
                        name: "FK_enrichment_round_enrichment_target_target_id",
                        column: x => x.target_id,
                        principalTable: "enrichment_target",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "meaning_submission_ai_review",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    attempt_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    submission_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    verdict = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    part_of_speech = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    meaning = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    phonetic_uk = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    phonetic_us = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    comment = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    model = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    reviewed_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    applied = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_submission_ai_review", x => x.id);
                    table.CheckConstraint("CK_meaning_submission_ai_review_verdict", "verdict IN ('accept', 'revise', 'reject')");
                    table.ForeignKey(
                        name: "FK_meaning_submission_ai_review_meaning_submission_submission_~",
                        column: x => x.submission_id,
                        principalTable: "meaning_submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "meaning_submission_auto_review",
                columns: table => new
                {
                    submission_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    attempt_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, collation: "C"),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    lease_until_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    next_attempt_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    last_failure_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true, collation: "C"),
                    last_failure_at_unix_ms = table.Column<long>(type: "bigint", nullable: true),
                    validated_review_json = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_submission_auto_review", x => x.submission_id);
                    table.ForeignKey(
                        name: "FK_meaning_submission_auto_review_meaning_submission_submissio~",
                        column: x => x.submission_id,
                        principalTable: "meaning_submission",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_generated_word",
                columns: table => new
                {
                    word_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    task_id = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_generated_word", x => x.word_id);
                    table.ForeignKey(
                        name: "FK_enrichment_generated_word_vocabulary_word_id",
                        column: x => x.word_id,
                        principalTable: "vocabulary",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_relation",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source_word_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    target_word_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    type = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    usage_note = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "needs_review", collation: "C"),
                    review_reason = table.Column<string>(type: "text", nullable: true, defaultValue: "new_relation", collation: "C"),
                    origin_kind = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    origin_reference = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    created_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    last_confirmed_at_unix_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_relation", x => x.id);
                    table.CheckConstraint("CK_vocabulary_relation_confirmation", "(status = 'confirmed' AND review_reason IS NULL AND last_confirmed_at_unix_ms IS NOT NULL) OR (status = 'needs_review' AND review_reason IS NOT NULL)");
                    table.CheckConstraint("CK_vocabulary_relation_id", "length(id) BETWEEN 1 AND 64 AND id = trim(id)");
                    table.CheckConstraint("CK_vocabulary_relation_origin", "origin_kind IN ('manual', 'import', 'generated')");
                    table.CheckConstraint("CK_vocabulary_relation_origin_reference", "origin_reference IS NULL OR (length(origin_reference) BETWEEN 1 AND 200 AND origin_reference = trim(origin_reference))");
                    table.CheckConstraint("CK_vocabulary_relation_review_reason", "review_reason IS NULL OR review_reason IN ('new_relation', 'word_changed', 'relation_changed')");
                    table.CheckConstraint("CK_vocabulary_relation_status", "status IN ('confirmed', 'needs_review')");
                    table.CheckConstraint("CK_vocabulary_relation_times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms AND (last_confirmed_at_unix_ms IS NULL OR (last_confirmed_at_unix_ms >= created_at_unix_ms AND last_confirmed_at_unix_ms <= updated_at_unix_ms))");
                    table.CheckConstraint("CK_vocabulary_relation_type", "type IN ('past_tense', 'past_participle', 'ing_form', 'third_person_singular', 'plural', 'comparative', 'superlative', 'derived_noun', 'derived_verb', 'derived_adjective', 'derived_adverb')");
                    table.CheckConstraint("CK_vocabulary_relation_usage_note", "usage_note IS NULL OR (length(usage_note) BETWEEN 1 AND 2000 AND usage_note = trim(usage_note))");
                    table.CheckConstraint("CK_vocabulary_relation_version", "version >= 1");
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_source",
                        column: x => x.source_word_id,
                        principalTable: "vocabulary",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_target",
                        column: x => x.target_word_id,
                        principalTable: "vocabulary",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_destination",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    book_id = table.Column<string>(type: "text", nullable: true, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_destination", x => x.id);
                    table.ForeignKey(
                        name: "FK_enrichment_destination_vocabulary_book_book_id",
                        column: x => x.book_id,
                        principalTable: "vocabulary_book",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_book_unit",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    book_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_book_unit", x => x.id);
                    table.UniqueConstraint("AK_vocabulary_book_unit_id_book_id", x => new { x.id, x.book_id });
                    table.ForeignKey(
                        name: "FK_vocabulary_book_unit_vocabulary_book_book_id",
                        column: x => x.book_id,
                        principalTable: "vocabulary_book",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_meaning",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    vocabulary_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    book_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    part_of_speech = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    phonetic_uk = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    phonetic_us = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    meaning = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    normalized_part_of_speech = table.Column<string>(type: "text", nullable: false, computedColumnSql: "translate(trim(coalesce(part_of_speech, '')), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')", stored: true, collation: "C"),
                    normalized_meaning = table.Column<string>(type: "text", nullable: false, computedColumnSql: "trim(meaning)", stored: true, collation: "C"),
                    example = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    insertion_order = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_meaning", x => x.id);
                    table.UniqueConstraint("AK_vocabulary_meaning_id_book_id", x => new { x.id, x.book_id });
                    table.UniqueConstraint("AK_vocabulary_meaning_id_vocabulary_id", x => new { x.id, x.vocabulary_id });
                    table.ForeignKey(
                        name: "FK_vocabulary_meaning_vocabulary_book_book_id",
                        column: x => x.book_id,
                        principalTable: "vocabulary_book",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vocabulary_meaning_vocabulary_vocabulary_id",
                        column: x => x.vocabulary_id,
                        principalTable: "vocabulary",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_call",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    target_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    round = table.Column<int>(type: "integer", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    result = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    upload_idempotency_key = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    error_code = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    started_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    finished_at_unix_ms = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_call", x => x.id);
                    table.CheckConstraint("CK_enrichment_call_result", "result IN ('Started','Succeeded','Throttled','TransientFailure','TerminalFailure')");
                    table.CheckConstraint("CK_enrichment_call_service", "service IN ('text','tts')");
                    table.CheckConstraint("CK_enrichment_call_times", "started_at_unix_ms >= 0 AND ((result = 'Started' AND finished_at_unix_ms IS NULL) OR (result != 'Started' AND finished_at_unix_ms >= started_at_unix_ms))");
                    table.ForeignKey(
                        name: "FK_enrichment_call_enrichment_round_target_id_round",
                        columns: x => new { x.target_id, x.round },
                        principalTable: "enrichment_round",
                        principalColumns: new[] { "target_id", "number" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "enrichment_wait",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    target_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    round = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    error_code = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    recorded_at_unix_ms = table.Column<long>(type: "bigint", nullable: false),
                    next_attempt_at_unix_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrichment_wait", x => x.id);
                    table.CheckConstraint("CK_enrichment_wait_reason", "reason IN ('Disabled','ConfigurationMissing','PreparationPending','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')");
                    table.CheckConstraint("CK_enrichment_wait_times", "recorded_at_unix_ms >= 0 AND next_attempt_at_unix_ms >= recorded_at_unix_ms");
                    table.ForeignKey(
                        name: "FK_enrichment_wait_enrichment_round_target_id_round",
                        columns: x => new { x.target_id, x.round },
                        principalTable: "enrichment_round",
                        principalColumns: new[] { "target_id", "number" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "meaning_submission_review_check",
                columns: table => new
                {
                    record_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    checked_at_unix_ms = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_submission_review_check", x => x.record_id);
                    table.ForeignKey(
                        name: "FK_meaning_submission_review_check_meaning_submission_ai_revie~",
                        column: x => x.record_id,
                        principalTable: "meaning_submission_ai_review",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "meaning_phonetic_epoch",
                columns: table => new
                {
                    meaning_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    locale = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    word_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    epoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meaning_phonetic_epoch", x => new { x.meaning_id, x.locale });
                    table.CheckConstraint("CK_meaning_phonetic_epoch", "epoch > 0 AND locale IN ('en-GB','en-US')");
                    table.ForeignKey(
                        name: "FK_meaning_phonetic_epoch_vocabulary_meaning_meaning_id_word_id",
                        columns: x => new { x.meaning_id, x.word_id },
                        principalTable: "vocabulary_meaning",
                        principalColumns: new[] { "id", "vocabulary_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_audio_binding",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    word_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    meaning_id = table.Column<string>(type: "text", nullable: true, collation: "C"),
                    locale = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    asset_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source_revision = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_audio_binding", x => x.id);
                    table.CheckConstraint("CK_vocabulary_audio_binding_locale", "locale IN ('en-GB', 'en-US')");
                    table.CheckConstraint("CK_vocabulary_audio_binding_revision", "length(source_revision) = 64 AND source_revision !~ '[^0-9a-f]'");
                    table.ForeignKey(
                        name: "FK_vocabulary_audio_binding_audio_asset_asset_id",
                        column: x => x.asset_id,
                        principalTable: "audio_asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_vocabulary_audio_binding_vocabulary_meaning_meaning_id_word~",
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

            migrationBuilder.CreateTable(
                name: "vocabulary_meaning_unit",
                columns: table => new
                {
                    unit_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    meaning_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    section = table.Column<string>(type: "text", nullable: false, defaultValue: "", collation: "C"),
                    entry_kind = table.Column<string>(type: "text", nullable: false, defaultValue: "", collation: "C"),
                    book_id = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_meaning_unit", x => new { x.unit_id, x.meaning_id, x.section, x.entry_kind });
                    table.CheckConstraint("CK_vocabulary_meaning_unit_entry_kind", "entry_kind IN ('', 'word', 'phrase')");
                    table.CheckConstraint("CK_vocabulary_meaning_unit_section", "section IN ('', 'A', 'B')");
                    table.ForeignKey(
                        name: "FK_vocabulary_meaning_unit_vocabulary_book_unit_unit_id_book_id",
                        columns: x => new { x.unit_id, x.book_id },
                        principalTable: "vocabulary_book_unit",
                        principalColumns: new[] { "id", "book_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_vocabulary_meaning_unit_vocabulary_meaning_meaning_id_book_~",
                        columns: x => new { x.meaning_id, x.book_id },
                        principalTable: "vocabulary_meaning",
                        principalColumns: new[] { "id", "book_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vocabulary_relation_meaning",
                columns: table => new
                {
                    relation_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    source_meaning_id = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    target_meaning_id = table.Column<string>(type: "text", nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vocabulary_relation_meaning", x => new { x.relation_id, x.source_meaning_id, x.target_meaning_id });
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_meaning_vocabulary_meaning_source_meani~",
                        column: x => x.source_meaning_id,
                        principalTable: "vocabulary_meaning",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_meaning_vocabulary_meaning_target_meani~",
                        column: x => x.target_meaning_id,
                        principalTable: "vocabulary_meaning",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_vocabulary_relation_meaning_vocabulary_relation_relation_id",
                        column: x => x.relation_id,
                        principalTable: "vocabulary_relation",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_session_expires_at_unix_ms_handle_hash",
                table: "admin_session",
                columns: new[] { "expires_at_unix_ms", "handle_hash" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_batch_kind_configuration_version_finished_at_uni~",
                table: "ai_request_batch",
                columns: new[] { "kind", "configuration_version", "finished_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_batch_outcome",
                table: "ai_request_batch",
                column: "outcome");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_batch_member_batch_id",
                table: "ai_request_batch_member",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "IX_ai_request_batch_member_target_id",
                table: "ai_request_batch_member",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "IX_audio_asset_bucket_object_key",
                table: "audio_asset",
                columns: new[] { "bucket", "object_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audio_invalidation_state_next_attempt_at_unix_ms",
                table: "audio_invalidation",
                columns: new[] { "state", "next_attempt_at_unix_ms" });

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
                name: "IX_business_api_key_secret_hash",
                table: "business_api_key",
                column: "secret_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_call_result_finished_at_unix_ms",
                table: "enrichment_call",
                columns: new[] { "result", "finished_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_call_service_result_finished_at_unix_ms",
                table: "enrichment_call",
                columns: new[] { "service", "result", "finished_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_call_target_id_round",
                table: "enrichment_call",
                columns: new[] { "target_id", "round" },
                unique: true,
                filter: "result = 'Started'");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_call_upload_idempotency_key",
                table: "enrichment_call",
                column: "upload_idempotency_key",
                unique: true,
                filter: "upload_idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_destination_book_id",
                table: "enrichment_destination",
                column: "book_id");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_recovery_target_id_input_revision_configuration_~",
                table: "enrichment_recovery",
                columns: new[] { "target_id", "input_revision", "configuration_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_round_state_next_attempt_at_unix_ms",
                table: "enrichment_round",
                columns: new[] { "state", "next_attempt_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_round_target_id_retry_key",
                table: "enrichment_round",
                columns: new[] { "target_id", "retry_key" },
                unique: true,
                filter: "retry_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_service_lease_service_id_configuration_version_e~",
                table: "enrichment_service_lease",
                columns: new[] { "service_id", "configuration_version", "expires_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_service_probe_service_id_started_at_unix_ms",
                table: "enrichment_service_probe",
                columns: new[] { "service_id", "started_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_target_word_id_locale_step",
                table: "enrichment_target",
                columns: new[] { "word_id", "locale", "step" },
                unique: true,
                filter: "meaning_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_target_word_id_meaning_id",
                table: "enrichment_target",
                columns: new[] { "word_id", "meaning_id" },
                unique: true,
                filter: "step = 'Expand'");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_target_word_id_meaning_id_locale_step",
                table: "enrichment_target",
                columns: new[] { "word_id", "meaning_id", "locale", "step" },
                unique: true,
                filter: "meaning_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_enrichment_wait_target_id_round",
                table: "enrichment_wait",
                columns: new[] { "target_id", "round" });

            migrationBuilder.CreateIndex(
                name: "IX_meaning_phonetic_epoch_meaning_id_word_id",
                table: "meaning_phonetic_epoch",
                columns: new[] { "meaning_id", "word_id" });

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_key",
                table: "meaning_submission",
                columns: new[] { "normalized_word", "normalized_part_of_speech", "normalized_meaning" });

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_pending_key",
                table: "meaning_submission",
                columns: new[] { "normalized_word", "normalized_part_of_speech", "normalized_meaning" },
                unique: true,
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_status_created_at_unix_ms",
                table: "meaning_submission",
                columns: new[] { "status", "created_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_ai_review_attempt_id",
                table: "meaning_submission_ai_review",
                column: "attempt_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_ai_review_submission_id_reviewed_at_unix~",
                table: "meaning_submission_ai_review",
                columns: new[] { "submission_id", "reviewed_at_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "IX_meaning_submission_auto_review_next_attempt_at_unix_ms_leas~",
                table: "meaning_submission_auto_review",
                columns: new[] { "next_attempt_at_unix_ms", "lease_until_unix_ms" });

            migrationBuilder.CreateIndex(
                name: "ix_service_audit_logs_action_occurred_at_utc_id",
                table: "service_audit_logs",
                columns: new[] { "action", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_service_audit_logs_occurred_at_utc_id",
                table: "service_audit_logs",
                columns: new[] { "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_service_audit_logs_operator_id_occurred_at_utc_id",
                table: "service_audit_logs",
                columns: new[] { "operator_id", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_service_audit_logs_target_id_occurred_at_utc_id",
                table: "service_audit_logs",
                columns: new[] { "target_id", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_service_audit_logs_target_occurred_at_utc_id",
                table: "service_audit_logs",
                columns: new[] { "target_type", "target_id", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_service_audit_logs_target_type_occurred_at_utc_id",
                table: "service_audit_logs",
                columns: new[] { "target_type", "occurred_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_insertion_order",
                table: "vocabulary",
                column: "insertion_order");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_normalized_word",
                table: "vocabulary",
                column: "normalized_word");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_word",
                table: "vocabulary",
                column: "word",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_book_unit_book_id_number",
                table: "vocabulary_book_unit",
                columns: new[] { "book_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_book_unit_id_book_id",
                table: "vocabulary_book_unit",
                columns: new[] { "id", "book_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_book_id_vocabulary_id",
                table: "vocabulary_meaning",
                columns: new[] { "book_id", "vocabulary_id" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_id_book_id",
                table: "vocabulary_meaning",
                columns: new[] { "id", "book_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_id_vocabulary_id",
                table: "vocabulary_meaning",
                columns: new[] { "id", "vocabulary_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_insertion_order",
                table: "vocabulary_meaning",
                column: "insertion_order");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_vocabulary_id",
                table: "vocabulary_meaning",
                column: "vocabulary_id");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_vocabulary_id_book_id_normalized_part_of~",
                table: "vocabulary_meaning",
                columns: new[] { "vocabulary_id", "book_id", "normalized_part_of_speech", "normalized_meaning" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_unit_meaning_id_book_id",
                table: "vocabulary_meaning_unit",
                columns: new[] { "meaning_id", "book_id" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_meaning_unit_unit_id_book_id",
                table: "vocabulary_meaning_unit",
                columns: new[] { "unit_id", "book_id" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_needs_review",
                table: "vocabulary_relation",
                columns: new[] { "updated_at_unix_ms", "id" },
                filter: "status = 'needs_review'");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_source_type_status",
                table: "vocabulary_relation",
                columns: new[] { "source_word_id", "type", "status", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_target_type_status",
                table: "vocabulary_relation",
                columns: new[] { "target_word_id", "type", "status", "id" });

            migrationBuilder.CreateIndex(
                name: "UX_vocabulary_relation_identity",
                table: "vocabulary_relation",
                columns: new[] { "source_word_id", "target_word_id", "type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_meaning_source_meaning_id",
                table: "vocabulary_relation_meaning",
                column: "source_meaning_id");

            migrationBuilder.CreateIndex(
                name: "IX_vocabulary_relation_meaning_target_meaning_id",
                table: "vocabulary_relation_meaning",
                column: "target_meaning_id");

            migrationBuilder.Sql(InitialPostgreSqlRules.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(InitialPostgreSqlRules.Down);

            migrationBuilder.DropTable(
                name: "admin_session");

            migrationBuilder.DropTable(
                name: "ai_request_batch_member");

            migrationBuilder.DropTable(
                name: "ai_request_batch_policy");

            migrationBuilder.DropTable(
                name: "audio_invalidation");

            migrationBuilder.DropTable(
                name: "audio_upload_operation");

            migrationBuilder.DropTable(
                name: "business_api_key");

            migrationBuilder.DropTable(
                name: "enrichment_call");

            migrationBuilder.DropTable(
                name: "enrichment_destination");

            migrationBuilder.DropTable(
                name: "enrichment_discovery");

            migrationBuilder.DropTable(
                name: "enrichment_generated_word");

            migrationBuilder.DropTable(
                name: "enrichment_recovery");

            migrationBuilder.DropTable(
                name: "enrichment_service_lease");

            migrationBuilder.DropTable(
                name: "enrichment_service_probe");

            migrationBuilder.DropTable(
                name: "enrichment_wait");

            migrationBuilder.DropTable(
                name: "meaning_phonetic_epoch");

            migrationBuilder.DropTable(
                name: "meaning_submission_auto_review");

            migrationBuilder.DropTable(
                name: "meaning_submission_review_check");

            migrationBuilder.DropTable(
                name: "service_audit_logs");

            migrationBuilder.DropTable(
                name: "service_data_protection_keys");

            migrationBuilder.DropTable(
                name: "vocabulary_audio_binding");

            migrationBuilder.DropTable(
                name: "vocabulary_meaning_unit");

            migrationBuilder.DropTable(
                name: "vocabulary_relation_meaning");

            migrationBuilder.DropTable(
                name: "ai_request_batch");

            migrationBuilder.DropTable(
                name: "enrichment_service");

            migrationBuilder.DropTable(
                name: "enrichment_round");

            migrationBuilder.DropTable(
                name: "meaning_submission_ai_review");

            migrationBuilder.DropTable(
                name: "audio_asset");

            migrationBuilder.DropTable(
                name: "vocabulary_book_unit");

            migrationBuilder.DropTable(
                name: "vocabulary_meaning");

            migrationBuilder.DropTable(
                name: "vocabulary_relation");

            migrationBuilder.DropTable(
                name: "enrichment_target");

            migrationBuilder.DropTable(
                name: "meaning_submission");

            migrationBuilder.DropTable(
                name: "vocabulary_book");

            migrationBuilder.DropTable(
                name: "vocabulary");
        }
    }
}
