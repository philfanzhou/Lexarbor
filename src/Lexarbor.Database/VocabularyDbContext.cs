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
