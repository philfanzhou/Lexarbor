using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lexarbor.Database;

internal static class AudioModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        var locale = new ValueConverter<AudioLocale, string>(value => value.ToCode(), value => AudioLocales.FromCode(value));
        modelBuilder.Entity<AudioAssetEntity>(entity =>
        {
            entity.HasIndex(asset => new { asset.Bucket, asset.ObjectKey }).IsUnique();
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_audio_asset_size", "byte_length > 0");
                table.HasCheckConstraint("CK_audio_asset_content_type", "content_type = 'audio/mpeg'");
                table.HasCheckConstraint("CK_audio_asset_sha256", "length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'");
                table.HasCheckConstraint("CK_audio_asset_location", "length(bucket) > 0 AND length(object_key) > 0");
                table.HasCheckConstraint("CK_audio_asset_times", "created_at_unix_ms >= 0 AND (first_published_at_unix_ms IS NULL OR first_published_at_unix_ms >= created_at_unix_ms)");
            });
        });
        modelBuilder.Entity<VocabularyAudioBindingEntity>(entity =>
        {
            entity.Property(binding => binding.Locale).HasConversion(locale);
            entity.HasIndex(binding => new { binding.WordId, binding.Locale }).IsUnique().HasFilter("meaning_id IS NULL");
            entity.HasIndex(binding => new { binding.MeaningId, binding.Locale }).IsUnique().HasFilter("meaning_id IS NOT NULL");
            entity.HasOne<VocabularyEntity>().WithMany().HasForeignKey(binding => binding.WordId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<VocabularyMeaningEntity>().WithMany()
                .HasForeignKey(binding => new { binding.MeaningId, binding.WordId })
                .HasPrincipalKey(meaning => new { meaning.Id, meaning.VocabularyId }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AudioAssetEntity>().WithMany().HasForeignKey(binding => binding.AssetId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_vocabulary_audio_binding_locale", "locale IN ('en-GB', 'en-US')");
                table.HasCheckConstraint("CK_vocabulary_audio_binding_revision", "length(source_revision) = 64 AND source_revision NOT GLOB '*[^0-9a-f]*'");
            });
        });
        modelBuilder.Entity<AudioUploadOperationEntity>(entity =>
        {
            entity.Property(operation => operation.Locale).HasConversion(locale);
            entity.Property(operation => operation.State).HasConversion<string>();
            entity.Property(operation => operation.CleanupReturnState).HasConversion<string>();
            // No target FK: deletion must keep request receipts and the known
            // object location recoverable. Only the independent asset is linked.
            entity.HasOne<AudioAssetEntity>().WithMany().HasForeignKey(operation => operation.AssetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(operation => operation.AssetId).IsUnique();
            entity.HasIndex(operation => new { operation.State, operation.CleanupNextAttemptAtUnixMs });
            entity.HasIndex(operation => new { operation.State, operation.UploadLeaseExpiresAtUnixMs });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_audio_upload_operation_key", "length(idempotency_key) = 36 AND substr(idempotency_key, 9, 1) = '-' AND substr(idempotency_key, 14, 1) = '-' AND substr(idempotency_key, 19, 1) = '-' AND substr(idempotency_key, 24, 1) = '-' AND length(replace(idempotency_key, '-', '')) = 32 AND replace(idempotency_key, '-', '') NOT GLOB '*[^0-9a-f]*'");
                table.HasCheckConstraint("CK_audio_upload_operation_locale", "locale IN ('en-GB', 'en-US')");
                table.HasCheckConstraint("CK_audio_upload_operation_state", "state IN ('Reserved', 'Transferring', 'Succeeded', 'Abandoned', 'CleanupClaimed', 'CleanupObserved')");
                table.HasCheckConstraint("CK_audio_upload_operation_cleanup_return", "cleanup_return_state IS NULL OR cleanup_return_state IN ('Abandoned', 'CleanupObserved')");
                table.HasCheckConstraint("CK_audio_upload_operation_fingerprint", "length(request_fingerprint) = 64 AND request_fingerprint NOT GLOB '*[^0-9a-f]*'");
                table.HasCheckConstraint("CK_audio_upload_operation_revision", "length(source_revision) = 64 AND source_revision NOT GLOB '*[^0-9a-f]*'");
                table.HasCheckConstraint("CK_audio_upload_operation_fences", "upload_lease_fence >= 0 AND cleanup_lease_fence >= 0 AND cleanup_attempts >= 0");
                table.HasCheckConstraint("CK_audio_upload_operation_times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms");
                table.HasCheckConstraint("CK_audio_upload_operation_receipt", "(state IN ('Reserved', 'Transferring') AND receipt_status_code IS NULL AND receipt_asset_id IS NULL AND receipt_source_revision IS NULL AND receipt_error_code IS NULL AND completed_at_unix_ms IS NULL) OR (state = 'Succeeded' AND receipt_status_code IS NOT NULL AND receipt_status_code = 200 AND receipt_asset_id IS NOT NULL AND receipt_source_revision IS NOT NULL AND completed_at_unix_ms IS NOT NULL AND receipt_asset_id = asset_id AND receipt_source_revision = source_revision AND receipt_error_code IS NULL) OR (state IN ('Abandoned', 'CleanupClaimed', 'CleanupObserved') AND receipt_status_code IS NOT NULL AND receipt_status_code IN (404, 409, 503) AND receipt_asset_id IS NULL AND receipt_source_revision IS NULL AND completed_at_unix_ms IS NOT NULL AND receipt_error_code IS NOT NULL)");
                table.HasCheckConstraint("CK_audio_upload_operation_upload_lease", "state NOT IN ('Reserved', 'Transferring') OR (upload_lease_owner IS NOT NULL AND upload_lease_expires_at_unix_ms IS NOT NULL AND upload_lease_fence > 0)");
                table.HasCheckConstraint("CK_audio_upload_operation_cleanup_lease", "state != 'CleanupClaimed' OR (cleanup_lease_owner IS NOT NULL AND cleanup_lease_expires_at_unix_ms IS NOT NULL AND cleanup_lease_fence > 0 AND cleanup_return_state IS NOT NULL)");
            });
        });
    }
}
