using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("textbook_audio_track")]
public sealed class TextbookAudioTrackEntity
{
    [Key, Column("id")] public string Id { get; set; } = string.Empty;
    [Column("book_id")] public string BookId { get; set; } = string.Empty;
    [Column("unit_id")] public string UnitId { get; set; } = string.Empty;
    [Column("title")] public string Title { get; set; } = string.Empty;
    [Column("display_order")] public int DisplayOrder { get; set; }
    [Column("current_asset_id")] public string? CurrentAssetId { get; set; }
    [Column("duration_seconds")] public double? DurationSeconds { get; set; }
    [Column("is_available")] public bool IsAvailable { get; set; }
    [Column("version")] public string Version { get; set; } = string.Empty;
}
