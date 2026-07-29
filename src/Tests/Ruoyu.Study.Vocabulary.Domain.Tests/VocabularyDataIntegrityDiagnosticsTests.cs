using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Ruoyu.Study.Vocabulary.Database;
using Xunit;

namespace Ruoyu.Study.Vocabulary.Domain.Tests;

public class VocabularyDataIntegrityDiagnosticsTests
{
    [Fact]
    public async Task InspectAndLogAsync_CleanDatabase_ReturnsZeroCounts()
    {
        await using var dbContext = CreateDbContext();

        var report = await VocabularyDataIntegrityDiagnostics.InspectAndLogAsync(
            dbContext,
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(0, report.NullBookIdCount);
        Assert.Equal(0, report.OrphanBookIdCount);
        Assert.Equal(0, report.NormalizedDuplicateWordGroupCount);
        Assert.Equal(0, report.DuplicateMeaningGroupCount);
        Assert.False(report.HasIssues);
    }

    [Fact]
    public void MeaningBookIntegritySql_IsNonDestructiveAndLegacyCompatible()
    {
        var sql = DatabaseInitializer.BuildMeaningBookIntegritySql();

        Assert.Contains("NOT VALID", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CK_vocabulary_meaning_book_id_required", sql, StringComparison.Ordinal);
        Assert.Contains("FK_vocabulary_meaning_vocabulary_book_book_id", sql, StringComparison.Ordinal);
        Assert.Contains("VALIDATE CONSTRAINT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SET NOT NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM vocabulary_meaning", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static VocabularyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new VocabularyDbContext(options);
    }
}
