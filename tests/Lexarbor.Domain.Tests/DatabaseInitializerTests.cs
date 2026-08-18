using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class DatabaseInitializerTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"lexarbor-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InitializeAsync_MissingDatabase_CreatesSchemaAndStarterBook()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "vocabulary.db");

        await using (var context = CreateContext(databasePath))
        {
            await DatabaseInitializer.InitializeAsync(
                context,
                NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(databasePath));
            Assert.Equal(1, await context.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(300, await context.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(300, await context.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
            Assert.All(
                await context.Vocabularies.ToListAsync(TestContext.Current.CancellationToken),
                item =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.PhoneticUk));
                    Assert.False(string.IsNullOrWhiteSpace(item.PhoneticUs));
                });
        }

        await using (var context = CreateContext(databasePath))
        {
            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = Guid.NewGuid().ToString(),
                Word = "persisted",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await DatabaseInitializer.InitializeAsync(
                context,
                NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

            Assert.Equal(301, await context.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await context.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task InitializeAsync_ExistingEmptyFile_MigratesWithoutLoadingSeed()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "existing.db");
        await File.WriteAllBytesAsync(databasePath, [], TestContext.Current.CancellationToken);

        await using var context = CreateContext(databasePath);
        await DatabaseInitializer.InitializeAsync(
            context,
            NullLoggerFactory.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(0, await context.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await context.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        var resolvedTemporaryDirectory = Path.GetFullPath(_temporaryDirectory);
        var resolvedSystemTemporaryDirectory = Path.GetFullPath(Path.GetTempPath());
        if (resolvedTemporaryDirectory.StartsWith(
                resolvedSystemTemporaryDirectory,
                StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(resolvedTemporaryDirectory))
        {
            Directory.Delete(resolvedTemporaryDirectory, recursive: true);
        }
    }

    private static VocabularyDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        return new VocabularyDbContext(options);
    }
}
