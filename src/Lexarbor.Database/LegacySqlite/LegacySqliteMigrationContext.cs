using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lexarbor.Database.LegacySqlite;

/// <summary>
/// Owns the SQLite migrations of the releases before PostgreSQL, unchanged, so
/// that a SQLite database from any of those releases can be brought to the
/// last SQLite schema before it is copied. It is used only on a private
/// working copy of the source database and never serves the application.
/// </summary>
/// <remarks>
/// The context has no entity types: the migrations carry their own target
/// models, and nothing queries through this context. The pending-model check
/// would compare that empty model with the last SQLite schema, so it is
/// switched off.
/// </remarks>
public sealed class LegacySqliteMigrationContext(DbContextOptions<LegacySqliteMigrationContext> options)
    : DbContext(options)
{
    /// <summary>The last migration of the last SQLite release.</summary>
    public const string LastMigration = "20261116010236_AddVocabularyRelationMeanings";

    internal static LegacySqliteMigrationContext Open(string connectionString) =>
        new(new DbContextOptionsBuilder<LegacySqliteMigrationContext>()
            .UseSqlite(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);
}
