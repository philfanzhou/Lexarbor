using Lexarbor.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database;

internal static class TextbookAudioUploadModelConfiguration
{
    private static string Uuid(string column) => $"length({column}) = 36 AND substr({column},9,1) = '-' AND substr({column},14,1) = '-' AND substr({column},19,1) = '-' AND substr({column},24,1) = '-' AND length(replace({column},'-','')) = 32 AND replace({column},'-','') NOT GLOB '*[^0-9a-f]*'";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TextbookAudioUploadOperationEntity>(entity =>
        {
            entity.Property(op => op.State).HasConversion<string>();
            entity.Property(op => op.CleanupReturnState).HasConversion<string>();
            // Historical target IDs have no FK: receipts and locations outlive directories.
            entity.HasOne<AudioAssetEntity>().WithMany().HasForeignKey(op => op.AssetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(op => op.AssetId).IsUnique();
            entity.HasIndex(op => new { op.State, op.CleanupNextAttemptAtUnixMs });
            entity.HasIndex(op => new { op.State, op.UploadLeaseExpiresAtUnixMs });
            entity.ToTable(table =>
            {
                const string prefix = "CK_textbook_audio_upload_";
                table.HasCheckConstraint(prefix + "key", Uuid("idempotency_key"));
                table.HasCheckConstraint(prefix + "expected_version", Uuid("expected_version"));
                table.HasCheckConstraint(prefix + "target", "length(trim(book_id)) > 0 AND book_id = trim(book_id) AND length(trim(unit_id)) > 0 AND unit_id = trim(unit_id) AND length(trim(track_id)) > 0 AND track_id = trim(track_id)");
                table.HasCheckConstraint(prefix + "fingerprint", "length(request_fingerprint) = 64 AND request_fingerprint NOT GLOB '*[^0-9a-f]*'");
                table.HasCheckConstraint(prefix + "duration", "duration_seconds > 0 AND duration_seconds <= 1.7976931348623157e308");
                table.HasCheckConstraint(prefix + "state", "state IN ('Reserved', 'Transferring', 'Succeeded', 'Abandoned', 'CleanupClaimed', 'CleanupObserved')");
                table.HasCheckConstraint(prefix + "fences", "typeof(upload_lease_fence) = 'integer' AND upload_lease_fence >= 1 AND typeof(cleanup_lease_fence) = 'integer' AND cleanup_lease_fence >= 0 AND typeof(cleanup_attempts) = 'integer' AND cleanup_attempts >= 0");
                table.HasCheckConstraint(prefix + "times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms AND (completed_at_unix_ms IS NULL OR completed_at_unix_ms >= created_at_unix_ms) AND (cleanup_next_attempt_at_unix_ms IS NULL OR cleanup_next_attempt_at_unix_ms >= 0) AND (cleanup_observed_at_unix_ms IS NULL OR cleanup_observed_at_unix_ms >= created_at_unix_ms)");
                table.HasCheckConstraint(prefix + "receipt", "(state IN ('Reserved', 'Transferring') AND receipt_status_code IS NULL AND receipt_asset_id IS NULL AND receipt_track_id IS NULL AND receipt_version IS NULL AND receipt_duration_seconds IS NULL AND receipt_error_code IS NULL AND completed_at_unix_ms IS NULL) OR (state = 'Succeeded' AND receipt_status_code IS NOT NULL AND receipt_status_code = 200 AND receipt_asset_id IS NOT NULL AND receipt_asset_id = asset_id AND receipt_track_id IS NOT NULL AND receipt_track_id = track_id AND receipt_version IS NOT NULL AND (" + Uuid("receipt_version") + ") AND receipt_duration_seconds IS NOT NULL AND receipt_duration_seconds = duration_seconds AND receipt_error_code IS NULL AND completed_at_unix_ms IS NOT NULL) OR (state IN ('Abandoned', 'CleanupClaimed', 'CleanupObserved') AND receipt_status_code IS NOT NULL AND receipt_status_code IN (404,409,503) AND receipt_asset_id IS NULL AND receipt_track_id IS NULL AND receipt_version IS NULL AND receipt_duration_seconds IS NULL AND receipt_error_code IS NOT NULL AND receipt_error_code IN ('audio.upload_failed','audio.upload_expired','audio.target_not_found','audio.target_conflict','audio.storage_unavailable','audio.upload_cancelled','audio.upload_timeout') AND completed_at_unix_ms IS NOT NULL)");
                table.HasCheckConstraint(prefix + "upload_lease", "(state IN ('Reserved','Transferring') AND upload_lease_owner IS NOT NULL AND length(trim(upload_lease_owner)) > 0 AND upload_lease_expires_at_unix_ms IS NOT NULL AND upload_lease_expires_at_unix_ms > created_at_unix_ms) OR (state NOT IN ('Reserved','Transferring') AND upload_lease_owner IS NULL AND upload_lease_expires_at_unix_ms IS NULL)");
                table.HasCheckConstraint(prefix + "cleanup_lease", "(state = 'CleanupClaimed' AND cleanup_lease_owner IS NOT NULL AND length(trim(cleanup_lease_owner)) > 0 AND cleanup_lease_expires_at_unix_ms IS NOT NULL AND cleanup_lease_expires_at_unix_ms > updated_at_unix_ms AND cleanup_lease_fence > 0 AND cleanup_return_state IS NOT NULL AND cleanup_return_state IN ('Abandoned','CleanupObserved')) OR (state != 'CleanupClaimed' AND cleanup_lease_owner IS NULL AND cleanup_lease_expires_at_unix_ms IS NULL AND cleanup_return_state IS NULL)");
            });
        });
    }
}
