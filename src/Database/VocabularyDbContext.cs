using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Vocabulary.Database.Entities;

namespace Ruoyu.Study.Vocabulary.Database;

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
            entity.Property(e => e.BookId).IsRequired();

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
