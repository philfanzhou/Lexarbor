using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

[Table("admin_session")]
public sealed class AdminSessionEntity
{
    [Key, Column("handle_hash")]
    public string HandleHash { get; set; } = string.Empty;
    [Column("expires_at_unix_ms")]
    public long ExpiresAtUnixMs { get; set; }
    [Column("protected_payload")]
    public string ProtectedPayload { get; set; } = string.Empty;
}
