using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Lexarbor.Domain.Models;

namespace Lexarbor.Database.Entities;

[Table("audio_asset")]
public sealed class AudioAssetEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("bucket")]
    public string Bucket { get; set; } = string.Empty;

    [Column("object_key")]
    public string ObjectKey { get; set; } = string.Empty;

    [Column("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [Column("byte_length")]
    public long ByteLength { get; set; }

    [Column("content_type")]
    public string ContentType { get; set; } = "audio/mpeg";

    [Column("created_at_unix_ms")]
    public long CreatedAtUnixMs { get; set; }

    [Column("first_published_at_unix_ms")]
    public long? FirstPublishedAtUnixMs { get; set; }

}
