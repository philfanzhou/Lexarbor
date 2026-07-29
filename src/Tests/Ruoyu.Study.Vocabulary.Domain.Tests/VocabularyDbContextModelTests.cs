using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Vocabulary.Database;
using Ruoyu.Study.Vocabulary.Database.Entities;
using Xunit;

namespace Ruoyu.Study.Vocabulary.Domain.Tests;

public class VocabularyDbContextModelTests
{
    [Fact]
    public void Meaning_HasRequiredBookForeignKey_WithRestrictDelete()
    {
        using var dbContext = CreateDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(VocabularyMeaningEntity));

        Assert.NotNull(entityType);
        Assert.False(entityType.FindProperty(nameof(VocabularyMeaningEntity.BookId))!.IsNullable);

        var bookForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(VocabularyBookEntity));

        Assert.Equal(DeleteBehavior.Restrict, bookForeignKey.DeleteBehavior);
    }

    [Fact]
    public void Meaning_HasCompositeBookAndVocabularyIndex()
    {
        using var dbContext = CreateDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(VocabularyMeaningEntity));

        Assert.NotNull(entityType);
        Assert.Contains(
            entityType.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(VocabularyMeaningEntity.BookId), nameof(VocabularyMeaningEntity.VocabularyId)]));
    }

    private static VocabularyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new VocabularyDbContext(options);
    }
}
