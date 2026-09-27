using System.Data.Common;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public class VocabularyCleanupTests : TestBase
{
    internal static VocabularyCleanupService Service(VocabularyDbContext db) => new(new VocabularyCleanupRepository(db), new UnitOfWork(db));
    internal static VocabularyCleanupSelection Selection(string action) => action switch
    {
        "removeMeaning" => new(action, "shared", "a1"),
        "removeWords" => new(action, WordIds: ["shared", "only-a", "shared"]),
        "delete" => new(action, ConfirmedBookName: "Starter English 300"),
        _ => new(action)
    };

    [Theory]
    [InlineData("removeMeaning", 1, 1, 0)]
    [InlineData("removeWords", 2, 3, 1)]
    [InlineData("clear", 2, 3, 1)]
    [InlineData("delete", 2, 3, 1)]
    public async Task PreviewAndCommit_FollowSameSets_KeepDisabledReferencesAndHistoricalOrphans(string action, int words, int meanings, int orphans)
    {
        await SeedAsync(_dbContext);
        var before = await StateAsync(_dbContext);
        var bookBefore = JsonSerializer.Serialize(await _dbContext.VocabularyBooks.AsNoTracking().SingleAsync(b => b.Id == "A", TestContext.Current.CancellationToken));
        var bBefore = JsonSerializer.Serialize(await _dbContext.VocabularyMeanings.AsNoTracking().Where(m => m.BookId == "B").ToListAsync(TestContext.Current.CancellationToken));
        var preview = await Service(_dbContext).PreviewAsync("A", Selection(action), TestContext.Current.CancellationToken);
        Assert.Equal((words, meanings, orphans), (preview.AffectedWordCount, preview.MeaningCount, preview.OrphanWordCount));
        Assert.Equal(before, await StateAsync(_dbContext));
        var result = await Service(_dbContext).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken);
        Assert.Equal((words, meanings, orphans, action == "delete"), (result.AffectedWordCount, result.DeletedMeaningCount, result.DeletedWordCount, result.DeletedBook));
        Assert.True(await _dbContext.Vocabularies.AnyAsync(v => v.Id == "shared", TestContext.Current.CancellationToken));
        Assert.True(await _dbContext.Vocabularies.AnyAsync(v => v.Id == "historical", TestContext.Current.CancellationToken));
        Assert.Equal(bBefore, JsonSerializer.Serialize(await _dbContext.VocabularyMeanings.AsNoTracking().Where(m => m.BookId == "B").ToListAsync(TestContext.Current.CancellationToken)));
        if (action != "delete") Assert.Equal(bookBefore, JsonSerializer.Serialize(await _dbContext.VocabularyBooks.AsNoTracking().SingleAsync(b => b.Id == "A", TestContext.Current.CancellationToken)));
        else Assert.False(await _dbContext.VocabularyBooks.AnyAsync(b => b.Id == "A", TestContext.Current.CancellationToken));
        await ForeignKeysAsync(_dbContext);
        Assert.Equal(0, await TempTableCountAsync(_dbContext));
    }

    [Fact]
    public async Task LastMeaningDeletesOnlyItsWord_EmptyBooksAndReplayAreExplicit()
    {
        await SeedAsync(_dbContext);
        var last = new VocabularyCleanupSelection("removeMeaning", "only-a", "a-only");
        Assert.Equal(1, (await Service(_dbContext).CommitAsync("A", last, TestContext.Current.CancellationToken)).DeletedWordCount);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service(_dbContext).CommitAsync("A", last, TestContext.Current.CancellationToken));
        await Service(_dbContext).CommitAsync("A", new("clear"), TestContext.Current.CancellationToken);
        var empty = await Service(_dbContext).PreviewAsync("A", new("clear"), TestContext.Current.CancellationToken);
        Assert.Equal((0, 0, 0), (empty.AffectedWordCount, empty.MeaningCount, empty.OrphanWordCount));
        Assert.Equal(0, (await Service(_dbContext).CommitAsync("A", new("clear"), TestContext.Current.CancellationToken)).DeletedMeaningCount);
        Assert.True((await Service(_dbContext).CommitAsync("A", Selection("delete"), TestContext.Current.CancellationToken)).DeletedBook);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service(_dbContext).CommitAsync("A", Selection("delete"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangedPreviewAndName_CommitRevalidatesCurrentData()
    {
        await SeedAsync(_dbContext);
        var preview = await Service(_dbContext).PreviewAsync("A", new("clear"), TestContext.Current.CancellationToken);
        _dbContext.VocabularyMeanings.Add(new VocabularyMeaningEntity { Id = "extra", VocabularyId = "only-b", BookId = "A", Meaning = "extra" });
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var committed = await Service(_dbContext).CommitAsync("A", new("clear"), TestContext.Current.CancellationToken);
        Assert.Equal(preview.MeaningCount + 1, committed.DeletedMeaningCount);
        await _dbContext.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET book_name='Renamed' WHERE id='A'", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ConflictException>(() => Service(_dbContext).CommitAsync("A", Selection("delete"), TestContext.Current.CancellationToken));
        Assert.True(await _dbContext.VocabularyBooks.AnyAsync(b => b.Id == "A", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidSelectionsOwnershipAndCancellation_DoNotWrite_AndLegacyDeleteStillConflicts()
    {
        await SeedAsync(_dbContext);
        var before = await StateAsync(_dbContext);
        foreach (var selection in new[] { new VocabularyCleanupSelection("unknown"), new("removeWords", WordIds: []), new("removeWords", WordIds: Enumerable.Repeat("shared", 101).ToArray()), new("clear", WordId: "shared") })
            await Assert.ThrowsAsync<DomainValidationException>(() => Service(_dbContext).CommitAsync("A", selection, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service(_dbContext).CommitAsync("missing", new("clear"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service(_dbContext).CommitAsync("A", new("removeMeaning", "missing", "a1"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service(_dbContext).CommitAsync("A", new("removeMeaning", "shared", "missing"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ConflictException>(() => Service(_dbContext).CommitAsync("B", Selection("removeMeaning"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ConflictException>(() => Service(_dbContext).CommitAsync("A", new("removeMeaning", "only-a", "a1"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ConflictException>(() => Service(_dbContext).CommitAsync("A", new("removeWords", WordIds: ["shared", "missing"]), TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(_dbContext).CommitAsync("A", new("clear"), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(_dbContext).PreviewAsync("A", new("clear"), cancelled.Token));
        await Assert.ThrowsAsync<ConflictException>(() => new VocabularyBookDomainService(_bookRepository, _unitOfWork).DeleteAsync("A"));
        Assert.Equal(before, await StateAsync(_dbContext));
    }

    [Theory]
    [InlineData("vocabulary_meaning")]
    [InlineData("vocabulary")]
    [InlineData("vocabulary_book")]
    public async Task FailureAfterEachDelete_RollsBackAllTablesAndClearsTemporaryState(string table)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var failure = new FailAfterDelete(table);
        var options = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite(connection).AddInterceptors(failure).Options;
        await using var context = new VocabularyDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await SeedAsync(context);
        var before = await StateAsync(context);
        failure.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(context).CommitAsync("A", Selection("delete"), TestContext.Current.CancellationToken));
        Assert.Equal(before, await StateAsync(context));
        Assert.Equal(0, await TempTableCountAsync(context));
        await ForeignKeysAsync(context);
        Assert.True((await Service(context).CommitAsync("A", Selection("delete"), TestContext.Current.CancellationToken)).DeletedBook);
    }

    [Fact]
    public async Task ConstraintFailure_RollsBackRemovedMeanings()
    {
        await SeedAsync(_dbContext);
        var before = await StateAsync(_dbContext);
        await _dbContext.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_delete BEFORE DELETE ON vocabulary BEGIN SELECT RAISE(ABORT,'synthetic'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ConflictException>(() => Service(_dbContext).CommitAsync("A", new("clear"), TestContext.Current.CancellationToken));
        Assert.Equal(before, await StateAsync(_dbContext));
        Assert.Equal(0, await TempTableCountAsync(_dbContext));
    }

    internal static async Task SeedAsync(VocabularyDbContext db)
    {
        db.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "Starter English 300", Status = true, Description = "synthetic legacy book", Category = "test", DisplayOrder = 8 },
            new VocabularyBookEntity { Id = "B", BookName = "B", Status = false });
        db.Vocabularies.AddRange(new[] { ("shared", "shared"), ("only-a", "exclusive"), ("only-b", "other"), ("historical", "historical") }.Select(v => new VocabularyEntity { Id = v.Item1, Word = v.Item2, PhoneticUk = "uk" }));
        db.VocabularyMeanings.AddRange(new[] { ("a1", "shared", "A"), ("a2", "shared", "A"), ("a-only", "only-a", "A"), ("b1", "shared", "B"), ("b-only", "only-b", "B") }
            .Select(m => new VocabularyMeaningEntity { Id = m.Item1, VocabularyId = m.Item2, BookId = m.Item3, Meaning = m.Item1, Example = "example" }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
    internal static async Task<string> StateAsync(VocabularyDbContext db) => JsonSerializer.Serialize(new
    {
        Books = await db.VocabularyBooks.AsNoTracking().OrderBy(b => b.Id).ToListAsync(TestContext.Current.CancellationToken),
        Words = await db.Vocabularies.AsNoTracking().OrderBy(v => v.Id).ToListAsync(TestContext.Current.CancellationToken),
        Meanings = await db.VocabularyMeanings.AsNoTracking().OrderBy(m => m.Id).ToListAsync(TestContext.Current.CancellationToken)
    });
    internal static async Task ForeignKeysAsync(VocabularyDbContext db)
    {
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
    internal static async Task<int> TempTableCountAsync(VocabularyDbContext db)
    {
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_temp_master WHERE name='lexarbor_cleanup_affected'";
            return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
    private sealed class FailAfterDelete(string table) : DbCommandInterceptor
    {
        public bool Enabled;
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.StartsWith($"DELETE FROM {table} ", StringComparison.Ordinal))
            { Enabled = false; throw new InvalidOperationException("Synthetic failure after delete."); }
            return ValueTask.FromResult(result);
        }
    }
}
