using System.Data.Common;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class VocabularyPublicPronunciationSnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConcurrentPhoneticEdit_LeavesEntriesAndPronunciationInOneSnapshot()
    {
        using var file = new AudioTestDatabase();
        await SeedAsync(file);
        await using var db = file.Open(new AfterBookRead(async () =>
        {
            await using var writer = file.Open();
            await writer.Database.ExecuteSqlRawAsync("""
                UPDATE vocabulary SET phonetic_us='new-default' WHERE id='word';
                UPDATE vocabulary_meaning SET phonetic_uk='new-specific' WHERE id='meaning';
                """, Ct);
        }));
        var repository = new VocabularyPublicQueryRepository(db);
        var page = await repository.GetEntriesAsync("book", "unit", 1, 20, Ct);
        var entry = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("us", entry.PhoneticUs);
        Assert.Equal(new VocabularyPronunciationAccent("specific", "meaning"), entry.Pronunciation.Uk);
        Assert.Equal(new VocabularyPronunciationAccent("us", "default"), entry.Pronunciation.Us);
        Assert.Null(db.Database.CurrentTransaction);
        var next = Assert.Single((await repository.GetEntriesAsync("book", "unit", 1, 20, Ct)).Items);
        Assert.Equal("new-specific", next.Pronunciation.Uk.Phonetic);
        Assert.Equal("new-default", next.Pronunciation.Us.Phonetic);
    }

    [Fact]
    public async Task CancellationAfterBookRead_ReleasesEntriesSnapshotWithoutWrites()
    {
        using var file = new AudioTestDatabase();
        await SeedAsync(file);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var db = file.Open(new AfterBookRead(() => { cancel.Cancel(); return Task.CompletedTask; }));
        var repository = new VocabularyPublicQueryRepository(db);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetEntriesAsync("book", null, 1, 20, cancel.Token));
        Assert.Null(db.Database.CurrentTransaction);
        var entry = Assert.Single((await repository.GetEntriesAsync("book", "unit", 1, 20, Ct)).Items);
        Assert.Equal("specific", entry.Pronunciation.Uk.Phonetic);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static async Task SeedAsync(AudioTestDatabase file)
    {
        await using var db = file.Open();
        await db.Database.MigrateAsync(Ct);
        await AudioTestDatabase.SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_meaning SET phonetic_uk='specific' WHERE id='meaning'", Ct);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", Ct);
    }

    private sealed class AfterBookRead(Func<Task> action) : DbCommandInterceptor
    {
        private bool _fired;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_fired && command.CommandText.Contains("FROM \"vocabulary_book\"", StringComparison.Ordinal))
            {
                _fired = true;
                await action();
            }
            return result;
        }
    }
}
