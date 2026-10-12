using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Lexarbor.Domain.Models;

namespace Lexarbor.Database.Entities;

[Table("vocabulary_audio_binding")]
public sealed class VocabularyAudioBindingEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("word_id")]
    public string WordId { get; set; } = string.Empty;

    [Column("meaning_id")]
    public string? MeaningId { get; set; }

    [Column("locale")]
    public AudioLocale Locale { get; set; }

    [Column("asset_id")]
    public string AssetId { get; set; } = string.Empty;

    [Column("source_revision")]
    public string SourceRevision { get; set; } = string.Empty;

}
