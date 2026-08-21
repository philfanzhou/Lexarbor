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
        });
    }
}
