using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Persistence.Relational.DataProtection;
using ServiceMantle.Persistence.Relational.Mapping;

namespace Lexarbor.Database;

public class VocabularyDbContext : DbContext
{
    private readonly IEnrichmentChangeNotifier? _enrichmentChanges;
    private readonly HashSet<string> _changedWords = [];
    private readonly HashSet<EnrichmentTargetKey> _changedAudioTargets = [];
    private bool _catalogChanged;
    // A scoped EF context can own only one transaction. Other requests have their own context.
    internal SemaphoreSlim TransactionSlot { get; } = new(1, 1);

    public VocabularyDbContext(DbContextOptions<VocabularyDbContext> options, IEnrichmentChangeNotifier? enrichmentChanges = null) : base(options)
    {
        _enrichmentChanges = enrichmentChanges;
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries<MeaningSubmissionAiReviewEntity>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("AI review conclusions are immutable.");
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var id = entry.Entity switch { VocabularyEntity word => word.Id, VocabularyMeaningEntity meaning => meaning.VocabularyId, _ => null };
            if (id != null || entry.Entity is VocabularyMeaningUnitEntity) _catalogChanged = true;
            if (id != null && _changedWords.Count < 256) _changedWords.Add(id);
        }
        return base.SaveChangesAsync(cancellationToken);
    }

    internal void FinishEnrichmentChanges(bool committed)
    {
        var words = _changedWords.ToArray();
        var audioTargets = _changedAudioTargets.ToArray();
        var changed = _catalogChanged;
        _changedWords.Clear(); _changedAudioTargets.Clear(); _catalogChanged = false;
        // A wakeup cannot reverse an already committed business operation.
        if (committed && changed)
            try { _enrichmentChanges?.Notify(words, audioTargets); } catch (Exception) { }
    }
    internal void EnrichmentWordChanged(string wordId)
    { _catalogChanged = true; if (_changedWords.Count < 256) _changedWords.Add(wordId); }
    internal void EnrichmentAudioUnbound(string wordId, string? meaningId, AudioLocale locale)
    {
        EnrichmentWordChanged(wordId);
        if (_changedAudioTargets.Count < 256) _changedAudioTargets.Add(new(wordId, meaningId, locale, EnrichmentStep.Audio));
    }

    public DbSet<VocabularyEntity> Vocabularies { get; set; } = null!;
    public DbSet<VocabularyRelationEntity> VocabularyRelations { get; set; } = null!;
    public DbSet<VocabularyRelationMeaningEntity> VocabularyRelationMeanings { get; set; } = null!;
    public DbSet<VocabularyBookEntity> VocabularyBooks { get; set; } = null!;
    public DbSet<VocabularyMeaningEntity> VocabularyMeanings { get; set; } = null!;
    public DbSet<MeaningPhoneticEpochEntity> MeaningPhoneticEpochs { get; set; } = null!;
    public DbSet<VocabularyBookUnitEntity> VocabularyBookUnits { get; set; } = null!;
    public DbSet<VocabularyMeaningUnitEntity> VocabularyMeaningUnits { get; set; } = null!;

    public DbSet<AudioAssetEntity> AudioAssets { get; set; } = null!;
    public DbSet<VocabularyAudioBindingEntity> VocabularyAudioBindings { get; set; } = null!;
    public DbSet<AudioUploadOperationEntity> AudioUploadOperations { get; set; } = null!;

    public DbSet<AudioInvalidationEntity> AudioInvalidations { get; set; } = null!;

    public DbSet<AiRequestBatchEntity> AiRequestBatches { get; set; } = null!;
    public DbSet<AiRequestBatchPolicyEntity> AiRequestBatchPolicies { get; set; } = null!;
    public DbSet<EnrichmentRecoveryEntity> EnrichmentRecoveries { get; set; } = null!;
    public DbSet<AiRequestBatchMemberEntity> AiRequestBatchMembers { get; set; } = null!;
    public DbSet<EnrichmentServiceLeaseEntity> EnrichmentServiceLeases { get; set; } = null!;

    public DbSet<EnrichmentGeneratedWordEntity> EnrichmentGeneratedWords { get; set; } = null!;
    public DbSet<EnrichmentTargetEntity> EnrichmentTargets { get; set; } = null!;
    public DbSet<EnrichmentRoundEntity> EnrichmentRounds { get; set; } = null!;
    public DbSet<EnrichmentWaitEntity> EnrichmentWaits { get; set; } = null!;
    public DbSet<EnrichmentCallEntity> EnrichmentCalls { get; set; } = null!;
    public DbSet<EnrichmentDestinationEntity> EnrichmentDestinations { get; set; } = null!;
    public DbSet<EnrichmentDiscoveryEntity> EnrichmentDiscovery { get; set; } = null!;
    public DbSet<EnrichmentServiceEntity> EnrichmentServices { get; set; } = null!;
    public DbSet<EnrichmentServiceProbeEntity> EnrichmentServiceProbes { get; set; } = null!;

    public DbSet<BusinessApiKeyEntity> BusinessApiKeys { get; set; } = null!;

    public DbSet<AdminSessionEntity> AdminSessions { get; set; } = null!;

    public DbSet<LegacySqliteImportEntity> LegacySqliteImports { get; set; } = null!;

    public DbSet<MeaningSubmissionEntity> MeaningSubmissions { get; set; } = null!;

    public DbSet<MeaningSubmissionAiReviewEntity> MeaningSubmissionAiReviews { get; set; } = null!;
    public DbSet<MeaningSubmissionAutoReviewEntity> MeaningSubmissionAutoReviews { get; set; } = null!;

    public DbSet<MeaningSubmissionReviewCheckEntity> MeaningSubmissionReviewChecks { get; set; } = null!;

    /// <summary>
    /// The collation every text column uses. "C" compares bytes, which is what
    /// SQLite's default BINARY collation did, so ordering, pagination, equality,
    /// and unique indexes stay what they were whatever collation the database
    /// itself was created with. Under it <c>lower()</c> and <c>ILIKE</c> fold
    /// ASCII letters only, again matching SQLite.
    /// </summary>
    public const string TextCollation = "C";

    /// <summary>
    /// The shadow column that records insertion order on the catalog tables.
    /// SQLite answered "the newest rows" from the implicit rowid; PostgreSQL
    /// has no such column, so the order is an explicit, indexed identity.
    /// </summary>
    public const string InsertionOrderProperty = "InsertionOrder";

    /// <summary>
    /// Folds A–Z to a–z and nothing else, after trimming spaces: the value
    /// SQLite's <c>lower(trim(...))</c> produced. PostgreSQL's <c>lower()</c>
    /// folds every Unicode letter under a linguistic collation, which would
    /// make distinct words such as <c>CAFÉ</c> and <c>Café</c> collide in a
    /// unique index, so the generated columns spell the ASCII fold out.
    /// </summary>
    internal static string AsciiLowerTrim(string expression) =>
        $"translate(trim({expression}), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<string>().UseCollation(TextCollation);
        // timestamptz stores an instant; Npgsql refuses a non-UTC offset, so
        // every value is written as UTC.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        VocabularyRelationModelConfiguration.Configure(modelBuilder);
        AudioModelConfiguration.Configure(modelBuilder);
        EnrichmentModelConfiguration.Configure(modelBuilder);
        AiRequestBatchModelConfiguration.Configure(modelBuilder);
        // Data Protection keys live in the business database as authenticated
        // envelopes (sm:v1:) scoped to the lexarbor service id, replacing the
        // former plaintext file ring under data/admin-keys.
        modelBuilder.AddServiceMantleDataProtectionKeys();
        // Management audit rows (administrator sign-in, sign-out) share the
        // business database and its transactions.
        modelBuilder.AddServiceMantleManagementAudit(ManagementAuditDatabaseDialect.PostgreSql);
        modelBuilder.Entity<AdminSessionEntity>(entity =>
        {
            entity.HasIndex(e => new { e.ExpiresAtUnixMs, e.HandleHash });
        });

        modelBuilder.Entity<LegacySqliteImportEntity>(entity =>
        {
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_legacy_sqlite_import_singleton", $"id = {LegacySqliteImportEntity.SingletonId}");
                table.HasCheckConstraint("CK_legacy_sqlite_import_outcome",
                    $"outcome IN ('{LegacySqliteImportEntity.Imported}', '{LegacySqliteImportEntity.None}')");
                table.HasCheckConstraint("CK_legacy_sqlite_import_counts", "copied_tables >= 0 AND copied_rows >= 0");
            });
        });

        modelBuilder.Entity<BusinessApiKeyEntity>(entity =>
        {
            entity.HasIndex(key => key.SecretHash).IsUnique();
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_business_api_key_hash", "length(secret_hash) = 64 AND secret_hash !~ '[^0-9a-f]'");
                table.HasCheckConstraint("CK_business_api_key_caller", "length(caller_id) BETWEEN 1 AND 2048");
                table.HasCheckConstraint("CK_business_api_key_scopes", "catalog OR meaning_submissions OR learning");
                table.HasCheckConstraint("CK_business_api_key_times", "created_at_unix_ms >= 0 AND (revoked_at_unix_ms IS NULL OR revoked_at_unix_ms >= created_at_unix_ms)");
            });
        });

        modelBuilder.Entity<VocabularyEntity>(entity =>
        {
            entity.HasIndex(e => e.Word).IsUnique();

            // The application's identity for a word is its normalized form, and
            // the lookup that resolves an import to an existing word compares
            // against it. Indexed rather than computed per row so that the
            // comparison is a seek: lower(trim(word)) written into a predicate
            // makes IX_vocabulary_word unusable and scans the table instead.
            //
            // Not unique. Uniqueness here is a stronger constraint than the
            // table has today and a migration asserting it would fail on a
            // database that already holds two spellings of one word; that is a
            // separate decision from making the lookup indexable.
            entity.Property(e => e.NormalizedWord)
                .HasComputedColumnSql(AsciiLowerTrim("word"), stored: true);
            entity.HasIndex(e => e.NormalizedWord);
            ConfigureInsertionOrder(entity);
        });

        modelBuilder.Entity<VocabularyMeaningEntity>(entity =>
        {
            entity.HasIndex(e => e.VocabularyId);
            entity.HasIndex(e => new { e.BookId, e.VocabularyId });
            entity.HasIndex(e => new
            {
                e.VocabularyId,
                e.BookId,
                e.NormalizedPartOfSpeech,
                e.NormalizedMeaning
            })
                .IsUnique();
            entity.Property(e => e.BookId).IsRequired();
            entity.Property(e => e.NormalizedPartOfSpeech)
                .HasComputedColumnSql(
                    AsciiLowerTrim("coalesce(part_of_speech, '')"),
                    stored: true);
            entity.Property(e => e.NormalizedMeaning)
                .HasComputedColumnSql("trim(meaning)", stored: true);

            entity.HasOne(e => e.Vocabulary)
                  .WithMany()
                  .HasForeignKey(e => e.VocabularyId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Book)
                  .WithMany(e => e.Meanings)
                  .HasForeignKey(e => e.BookId)
                  .OnDelete(DeleteBehavior.Restrict);

            // Referenced by the composite foreign key from vocabulary_meaning_unit
            // as (id, book_id). Uniqueness of the wider pair is implied by the
            // primary key on id, so this constrains nothing the rows do not
            // already satisfy.
            entity.HasIndex(e => new { e.Id, e.BookId }).IsUnique();
            entity.HasIndex(e => new { e.Id, e.VocabularyId }).IsUnique();
            ConfigureInsertionOrder(entity);
        });

        modelBuilder.Entity<MeaningSubmissionReviewCheckEntity>(entity =>
        {
            entity.HasOne<MeaningSubmissionAiReviewEntity>().WithMany().HasForeignKey(e => e.RecordId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MeaningSubmissionAutoReviewEntity>(entity =>
        {
            entity.HasOne<MeaningSubmissionEntity>().WithMany().HasForeignKey(e => e.SubmissionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.NextAttemptAtUnixMs, e.LeaseUntilUnixMs });
        });

        modelBuilder.Entity<MeaningSubmissionAiReviewEntity>(entity =>
        {
            entity.HasIndex(e => e.AttemptId).IsUnique();
            entity.HasIndex(e => new { e.SubmissionId, e.ReviewedAtUnixMs });
            entity.HasOne<MeaningSubmissionEntity>().WithMany().HasForeignKey(e => e.SubmissionId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint("CK_meaning_submission_ai_review_verdict", "verdict IN ('accept', 'revise', 'reject')"));
        });

        modelBuilder.Entity<MeaningSubmissionEntity>(entity =>
        {
            // The same normalization the catalog applies to a word and to the
            // logical key of a meaning, so a submission can be matched against
            // both. Stored, as the table is created with them.
            entity.Property(e => e.NormalizedWord)
                .HasComputedColumnSql(AsciiLowerTrim("word"), stored: true);
            entity.Property(e => e.NormalizedPartOfSpeech)
                .HasComputedColumnSql(AsciiLowerTrim("coalesce(part_of_speech, '')"), stored: true);
            entity.Property(e => e.NormalizedMeaning)
                .HasComputedColumnSql("trim(meaning)", stored: true);
            entity.Property(e => e.Status).HasConversion<int>();

            entity.HasIndex(
                e => new { e.NormalizedWord, e.NormalizedPartOfSpeech, e.NormalizedMeaning },
                "IX_meaning_submission_key");
            entity.HasIndex(e => new { e.Status, e.CreatedAtUnixMs });

            // At most one pending submission per definition: equivalent proposals
            // merge into it rather than queueing twice for review.
            entity.HasIndex(
                    e => new { e.NormalizedWord, e.NormalizedPartOfSpeech, e.NormalizedMeaning },
                    "IX_meaning_submission_pending_key")
                .HasFilter("status = 0")
                .IsUnique();

            entity.ToTable(table => table.HasCheckConstraint(
                "CK_meaning_submission_status", "status IN (0, 1, 2)"));
        });

        modelBuilder.Entity<VocabularyBookUnitEntity>(entity =>
        {
            // Unit numbers are unique within one book only; two books may both
            // have a Unit 2.
            entity.HasIndex(e => new { e.BookId, e.Number }).IsUnique();

            // The composite foreign key from vocabulary_meaning_unit references
            // (id, book_id); the referenced columns of a parent key need a
            // unique constraint, and the primary key on id alone does not cover
            // the pair. Uniqueness of the wider pair is implied by the
            // primary key, so this index adds a constraint the rows already
            // satisfy.
            entity.HasIndex(e => new { e.Id, e.BookId }).IsUnique();

            entity.HasOne(e => e.Book)
                  .WithMany()
                  .HasForeignKey(e => e.BookId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VocabularyMeaningUnitEntity>(entity =>
        {
            // The section and the entry kind complete the position: a meaning
            // may sit in the same unit's Section A and Section B, as a word and
            // as a phrase, as separate rows. Both properties are empty-string
            // sentinels rather than null because SQLite treats NULLs in a
            // composite primary key as distinct, which would break the
            // idempotent re-import of an unsectioned or unclassified
            // assignment.
            entity.HasKey(e => new { e.UnitId, e.MeaningId, e.Section, e.EntryKind });

            entity.Property(e => e.Section)
                .HasDefaultValue(string.Empty);

            entity.Property(e => e.EntryKind)
                .HasDefaultValue(string.Empty);

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_vocabulary_meaning_unit_section",
                    "section IN ('', 'A', 'B')");
                table.HasCheckConstraint(
                    "CK_vocabulary_meaning_unit_entry_kind",
                    "entry_kind IN ('', 'word', 'phrase')");
            });

            // Both foreign keys include book_id and point at a (id, book_id)
            // parent key, so a row can only exist when the unit and the meaning
            // belong to the same book. This is what makes a cross-book
            // assignment unrepresentable at the database level; the domain
            // service rejects one earlier with a clearer message, and this is
            // the backstop for anything that writes past it.
            entity.HasOne(e => e.Unit)
                  .WithMany()
                  .HasForeignKey(e => new { e.UnitId, e.BookId })
                  .HasPrincipalKey(unit => new { unit.Id, unit.BookId })
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Meaning)
                  .WithMany()
                  .HasForeignKey(e => new { e.MeaningId, e.BookId })
                  .HasPrincipalKey(meaning => new { meaning.Id, meaning.BookId })
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Properties stored as text through a value conversion (the enums
        // persisted by name) are not string properties to the convention in
        // ConfigureConventions, so they take the byte-wise collation here.
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            var storeType = property.GetProviderClrType() ?? property.GetValueConverter()?.ProviderClrType ?? property.ClrType;
            if (storeType == typeof(string) && property.GetCollation() is null)
            {
                property.SetCollation(TextCollation);
            }
        }
    }

    private static void ConfigureInsertionOrder<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity)
        where TEntity : class
    {
        entity.Property<long>(InsertionOrderProperty)
            .HasColumnName("insertion_order")
            .UseIdentityByDefaultColumn();
        entity.HasIndex(InsertionOrderProperty);
    }
}

internal sealed class UtcDateTimeOffsetConverter()
    : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>(
        value => value.ToUniversalTime(),
        value => value);
