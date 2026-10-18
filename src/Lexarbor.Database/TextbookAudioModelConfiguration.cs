using Lexarbor.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database;

internal static class TextbookAudioModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TextbookAudioTrackEntity>(entity =>
        {
            entity.HasOne<VocabularyBookEntity>().WithMany().HasForeignKey(t => t.BookId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<VocabularyBookUnitEntity>().WithMany().HasForeignKey(t => new { t.UnitId, t.BookId })
                .HasPrincipalKey(u => new { u.Id, u.BookId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AudioAssetEntity>().WithMany().HasForeignKey(t => t.CurrentAssetId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(t => new { t.UnitId, t.DisplayOrder, t.Id });
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_textbook_audio_track_title", "length(trim(title)) > 0");
                table.HasCheckConstraint("CK_textbook_audio_track_order", "display_order >= 0 AND display_order <= 2147483647 AND typeof(display_order) = 'integer'");
                table.HasCheckConstraint("CK_textbook_audio_track_available", "is_available IN (0, 1)");
                table.HasCheckConstraint("CK_textbook_audio_track_file", "(current_asset_id IS NULL AND duration_seconds IS NULL AND is_available = 0) OR (current_asset_id IS NOT NULL AND duration_seconds IS NOT NULL AND duration_seconds > 0 AND duration_seconds <= 1.7976931348623157e308)");
                table.HasCheckConstraint("CK_textbook_audio_track_version", "length(version) = 36 AND substr(version,9,1) = '-' AND substr(version,14,1) = '-' AND substr(version,19,1) = '-' AND substr(version,24,1) = '-' AND length(replace(version,'-','')) = 32 AND replace(version,'-','') NOT GLOB '*[^0-9a-f]*'");
            });
        });
    }
}
