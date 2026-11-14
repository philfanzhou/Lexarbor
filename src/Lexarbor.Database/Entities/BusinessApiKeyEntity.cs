using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("business_api_key")]
public sealed class BusinessApiKeyEntity
{
    [Key, Column("id")]
    public required string Id { get; init; }
    [Column("secret_hash")]
    public required string SecretHash { get; init; }
    [Column("caller_id")]
    public required string CallerId { get; init; }
    [Column("catalog")]
    public bool Catalog { get; init; }
    [Column("meaning_submissions")]
    public bool MeaningSubmissions { get; init; }
    [Column("created_at_unix_ms")]
    public long CreatedAtUnixMs { get; init; }
    [Column("revoked_at_unix_ms")]
    public long? RevokedAtUnixMs { get; init; }
}
