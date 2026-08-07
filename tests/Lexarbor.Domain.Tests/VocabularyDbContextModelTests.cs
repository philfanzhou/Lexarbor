using Microsoft.EntityFrameworkCore;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Xunit;

namespace Lexarbor.Domain.Tests;

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

    [Fact]
    public void Meaning_HasUniqueNormalizedLogicalKey()
    {
        using var dbContext = CreateDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(VocabularyMeaningEntity));

        Assert.NotNull(entityType);
        Assert.Contains(
            entityType.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name).SequenceEqual(
                     [
                         nameof(VocabularyMeaningEntity.VocabularyId),
                         nameof(VocabularyMeaningEntity.BookId),
                         nameof(VocabularyMeaningEntity.NormalizedPartOfSpeech),
                         nameof(VocabularyMeaningEntity.NormalizedMeaning)
                     ]));
    }

    [Fact]
    public void Context_UsesSqliteProvider()
    {
        using var dbContext = CreateDbContext();

        Assert.Equal(
            "Microsoft.EntityFrameworkCore.Sqlite",
            dbContext.Database.ProviderName);
    }

    private static VocabularyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        return new VocabularyDbContext(options);
    }
}
