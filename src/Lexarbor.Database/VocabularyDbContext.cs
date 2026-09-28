using Lexarbor.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database;

public class VocabularyDbContext : DbContext
{
    public VocabularyDbContext(DbContextOptions<VocabularyDbContext> options) : base(options)
    {
    }

    public DbSet<VocabularyEntity> Vocabularies { get; set; } = null!;
    public DbSet<VocabularyBookEntity> VocabularyBooks { get; set; } = null!;
    public DbSet<VocabularyMeaningEntity> VocabularyMeanings { get; set; } = null!;
    public DbSet<VocabularyBookUnitEntity> VocabularyBookUnits { get; set; } = null!;
    public DbSet<VocabularyMeaningUnitEntity> VocabularyMeaningUnits { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<VocabularyEntity>(entity =>
        {
            entity.HasIndex(e => e.Word).IsUnique();

            // The application's identity for a word is its normalized form, and
            // the lookup that resolves an import to an existing word compares
            // against it. Indexed rather than computed per row so that the
            // comparison is a seek: lower(trim(word)) written into a predicate
            // makes IX_vocabulary_word unusable and scans the table instead.
            //
            // Virtual, unlike the two generated columns on vocabulary_meaning,
            // which are stored. Those were declared in the CREATE TABLE of the
            // initial migration; SQLite refuses "ALTER TABLE ... ADD COLUMN" for
            // a stored generated column, so adding this one to a database that
            // already exists means either a virtual column or a full table
            // rebuild. The index holds the computed value either way, which is
            // what the lookup reads, so the query cost is the same and the
            // rebuild buys nothing.
            //
            // Not unique. Uniqueness here is a stronger constraint than the
            // table has today and a migration asserting it would fail on a
            // database that already holds two spellings of one word; that is a
            // separate decision from making the lookup indexable.
            entity.Property(e => e.NormalizedWord)
                .HasComputedColumnSql("lower(trim(word))", stored: false);
            entity.HasIndex(e => e.NormalizedWord);
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
                    "lower(trim(coalesce(part_of_speech, '')))",
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
            // as (id, book_id). SQLite accepts a unique index as a parent key, and
            // an index can be added to a table that already exists, which a table
            // constraint cannot on SQLite. Uniqueness of the wider pair is implied
            // by the primary key on id, so this constrains nothing the rows do not
            // already satisfy.
            entity.HasIndex(e => new { e.Id, e.BookId }).IsUnique();
        });

        modelBuilder.Entity<VocabularyBookUnitEntity>(entity =>
        {
            // Unit numbers are unique within one book only; two books may both
            // have a Unit 2.
            entity.HasIndex(e => new { e.BookId, e.Number }).IsUnique();

            // The composite foreign key from vocabulary_meaning_unit references
            // (id, book_id); SQLite requires the referenced columns of a parent
            // key to carry a unique index, and the primary key on id alone does
            // not cover the pair. Uniqueness of the wider pair is implied by the
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
            // The section completes the position: a meaning may sit in the
            // same unit's Section A and Section B as two rows. The property is
            // the empty-string sentinel rather than null because SQLite treats
            // NULLs in a composite primary key as distinct, which would break
            // the idempotent re-import of an unsectioned assignment.
            entity.HasKey(e => new { e.UnitId, e.MeaningId, e.Section });

            entity.Property(e => e.Section)
                .HasDefaultValue(string.Empty);

            entity.ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_vocabulary_meaning_unit_section",
                    "section IN ('', 'A', 'B')");
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
    }
}
