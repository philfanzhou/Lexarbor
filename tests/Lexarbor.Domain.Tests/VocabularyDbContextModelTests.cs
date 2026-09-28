using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public void BookUnit_HasUniqueBookAndNumberIndex()
    {
        using var dbContext = CreateDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(VocabularyBookUnitEntity));

        Assert.NotNull(entityType);
        Assert.Contains(
            entityType.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual([nameof(VocabularyBookUnitEntity.BookId), nameof(VocabularyBookUnitEntity.Number)]));
    }

    [Fact]
    public void BookUnit_BookForeignKey_CascadesOnDelete()
    {
        using var dbContext = CreateDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(VocabularyBookUnitEntity));

        Assert.NotNull(entityType);
        var bookForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(VocabularyBookEntity));
        Assert.Equal(DeleteBehavior.Cascade, bookForeignKey.DeleteBehavior);
    }

    [Fact]
    public void Meaning_HasUniqueCompositeIdBookIdIndex()
    {
        using var dbContext = CreateDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(VocabularyMeaningEntity));

        Assert.NotNull(entityType);
        Assert.Contains(
            entityType.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual([nameof(VocabularyMeaningEntity.Id), nameof(VocabularyMeaningEntity.BookId)]));
    }

    /// <summary>
    /// The membership's two composite foreign keys share the same <c>book_id</c>
    /// column and both cascade; together they make a cross-book assignment
    /// unrepresentable and keep deletions from leaving dangling rows.
    /// </summary>
    [Fact]
    public void MeaningUnit_CompositeForeignKeys_ShareBookIdAndCascade()
    {
        using var dbContext = CreateDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(VocabularyMeaningUnitEntity));

        Assert.NotNull(entityType);
        Assert.Equal(
            [nameof(VocabularyMeaningUnitEntity.UnitId), nameof(VocabularyMeaningUnitEntity.MeaningId)],
            entityType.FindPrimaryKey()!.Properties.Select(property => property.Name));

        var unitForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(VocabularyBookUnitEntity));
        Assert.Equal(
            [nameof(VocabularyMeaningUnitEntity.UnitId), nameof(VocabularyMeaningUnitEntity.BookId)],
            unitForeignKey.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(VocabularyBookUnitEntity.Id), nameof(VocabularyBookUnitEntity.BookId)],
            unitForeignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Cascade, unitForeignKey.DeleteBehavior);

        var meaningForeignKey = Assert.Single(
            entityType.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(VocabularyMeaningEntity));
        Assert.Equal(
            [nameof(VocabularyMeaningUnitEntity.MeaningId), nameof(VocabularyMeaningUnitEntity.BookId)],
            meaningForeignKey.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(VocabularyMeaningEntity.Id), nameof(VocabularyMeaningEntity.BookId)],
            meaningForeignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Cascade, meaningForeignKey.DeleteBehavior);
    }

    private static VocabularyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        return new VocabularyDbContext(options);
    }
}
