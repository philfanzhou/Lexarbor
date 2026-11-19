using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Database.Entities;

/// <summary>
/// The one row that records how this PostgreSQL database came to hold its
/// data: copied and verified from a SQLite database of an earlier release, or
/// initialized without one. The transition release wrote this marker.
/// </summary>
[Table("legacy_sqlite_import")]
public sealed class LegacySqliteImportEntity
{
    public const int SingletonId = 1;
    public const string Imported = "imported";
    public const string None = "none";

    [Key, Column("id")]
    public int Id { get; init; } = SingletonId;

    /// <summary><see cref="Imported"/> or <see cref="None"/>.</summary>
    [Column("outcome")]
    public required string Outcome { get; init; }

    /// <summary>The last SQLite migration the copied database carried, after the bundled migrations ran.</summary>
    [Column("source_schema_version")]
    public string? SourceSchemaVersion { get; init; }

    [Column("copied_tables")]
    public int CopiedTables { get; init; }

    [Column("copied_rows")]
    public long CopiedRows { get; init; }

    [Column("completed_at_unix_ms")]
    public long CompletedAtUnixMs { get; init; }
}
