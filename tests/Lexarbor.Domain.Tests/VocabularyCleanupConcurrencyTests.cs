using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using static Lexarbor.Domain.Tests.VocabularyCleanupTests;

namespace Lexarbor.Domain.Tests;

public class VocabularyCleanupConcurrencyTests
{
    [Theory]
    [InlineData(false, "clear")]
    [InlineData(true, "clear")]
    [InlineData(false, "delete")]
    [InlineData(true, "delete")]
    public async Task ImportAndCleanup_BothOrdersRecheckReferencesWithoutRevivingOldIds(bool cleanupFirst, string action)
    {
        await WithFileAsync(async options =>
        {
            await using var first = new VocabularyDbContext(options);
            await using var second = new VocabularyDbContext(options);
            await first.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=1 WHERE id='B'", TestContext.Current.CancellationToken);
            Func<Task> cleanup = async () => { await Service(cleanupFirst ? first : second).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken); };
            Func<Task> import = async () => { await ImportAsync(cleanupFirst ? second : first, "B"); };
            await OrderedAsync(first, cleanupFirst ? cleanup : import, cleanupFirst ? import : cleanup);
            await using var verify = new VocabularyDbContext(options);
            var word = await verify.Vocabularies.SingleAsync(v => v.Word == "exclusive", TestContext.Current.CancellationToken);
            Assert.Equal(!cleanupFirst, word.Id == "only-a");
            Assert.True(await verify.VocabularyMeanings.AnyAsync(m => m.BookId == "B" && m.VocabularyId == word.Id, TestContext.Current.CancellationToken));
            await ForeignKeysAsync(verify);
        });
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("delete")]
    [InlineData("removeWords")]
    [InlineData("removeMeaning")]
    public async Task TwoCleanups_SecondUsesCurrentMissingOrEmptyRules(string action)
    {
        await WithFileAsync(async options =>
        {
            await using var first = new VocabularyDbContext(options);
            await using var second = new VocabularyDbContext(options);
            await OrderedAsync(first,
                async () => { await Service(first).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken); },
                async () =>
                {
                    if (action == "clear") Assert.Equal(0, (await Service(second).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken)).DeletedMeaningCount);
                    else if (action == "removeWords") await Assert.ThrowsAsync<ConflictException>(() => Service(second).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken));
                    else await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service(second).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken));
                });
            await ForeignKeysAsync(first);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSingleEditAndCleanup_BothOrdersDoNotReviveDeletedIds(bool cleanupFirst)
    {
        await WithFileAsync(async options =>
        {
            await using var first = new VocabularyDbContext(options);
            await using var second = new VocabularyDbContext(options);
            Func<Task> cleanup = async () => { await Service(cleanupFirst ? first : second).CommitAsync("A", new("clear"), TestContext.Current.CancellationToken); };
            Func<Task> edit = async () =>
            {
                var db = cleanupFirst ? second : first;
                Func<Task> operation = async () =>
                {
                    await ImportService(db).AddOrUpdateAsync(new VocabularyModel { Id = "only-a", Word = "exclusive" },
                    new VocabularyMeaningModel { Id = "a-only", VocabularyId = "only-a", BookId = "A", Meaning = "changed" });
                };
                if (cleanupFirst) await Assert.ThrowsAsync<ResourceNotFoundException>(operation); else await operation();
            };
            await OrderedAsync(first, cleanupFirst ? cleanup : edit, cleanupFirst ? edit : cleanup);
            await using var verify = new VocabularyDbContext(options);
            Assert.False(await verify.Vocabularies.AnyAsync(v => v.Id == "only-a", TestContext.Current.CancellationToken));
            Assert.False(await verify.VocabularyMeanings.AnyAsync(m => m.Id == "a-only", TestContext.Current.CancellationToken));
        });
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("delete")]
    public async Task ImportAfterBookCleanup_FollowsBookExistence(string action)
    {
        await WithFileAsync(async options =>
        {
            await using var db = new VocabularyDbContext(options);
            await Service(db).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken);
            if (action == "delete") await Assert.ThrowsAsync<ResourceNotFoundException>(() => ImportAsync(db, "A"));
            else await ImportAsync(db, "A");
            await ForeignKeysAsync(db);
        });
    }

    [Fact]
    public async Task CancellationAfterDeletingMeanings_StillCommitsAllStages()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new DeleteBarrier(entered, release);
        await WithFileAsync(async options =>
        {
            await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>(options).AddInterceptors(hook).Options);
            using var disconnected = new CancellationTokenSource();
            var cleanup = Service(db).CommitAsync("A", Selection("delete"), disconnected.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                disconnected.Cancel();
            }
            finally { release.TrySetResult(); }
            var result = await cleanup;
            Assert.Equal((3, 1, true), (result.DeletedMeaningCount, result.DeletedWordCount, result.DeletedBook));
            await ForeignKeysAsync(db);
        });
    }

    [Fact]
    public async Task Preview_DoesNotTakeWriteLock_AndKeepsAConsistentWalSnapshot()
    {
        await WithFileAsync(async options =>
        {
            await using var writer = new VocabularyDbContext(options);
            await using var reader = new VocabularyDbContext(options);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var write = new UnitOfWork(writer).ExecuteInTransactionAsync(async () =>
            {
                await writer.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning WHERE book_id='A'");
                entered.SetResult(); await release.Task; return 0;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            try
            {
                var preview = await Service(reader).PreviewAsync("A", new("clear"), TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Equal(3, preview.MeaningCount);
                Assert.Equal(2, preview.AffectedWordCount);
            }
            finally { release.TrySetResult(); }
            await write;
        });
    }

    [Fact]
    public async Task Preview_WriterCommitsBetweenQueries_ResponseStillUsesOriginalSnapshot()
    {
        await WithFileAsync(async options =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reader = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>(options)
                .AddInterceptors(new ReadBarrier(entered, release)).Options);
            await using var writer = new VocabularyDbContext(options);
            var preview = Service(reader).PreviewAsync("A", new("clear"), TestContext.Current.CancellationToken);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                await new UnitOfWork(writer).ExecuteInTransactionAsync(async () =>
                {
                    await writer.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning WHERE book_id='A'", TestContext.Current.CancellationToken);
                    return 0;
                }).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }
            finally { release.TrySetResult(); }
            var result = await preview;
            Assert.Equal((2, 3, 1), (result.AffectedWordCount, result.MeaningCount, result.OrphanWordCount));
            Assert.Equal(0, await writer.VocabularyMeanings.CountAsync(m => m.BookId == "A", TestContext.Current.CancellationToken));
        });
    }

    private static async Task OrderedAsync(VocabularyDbContext first, Func<Task> firstOperation, Func<Task> secondOperation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = new UnitOfWork(first).ExecuteInTransactionAsync(async () => { await firstOperation(); entered.SetResult(); await release.Task; return 0; });
        Task? two = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            two = secondOperation(); Assert.False(two.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await one; if (two != null) await two;
    }
    internal static async Task WithFileAsync(Func<DbContextOptions<VocabularyDbContext>, Task> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexarbor-cleanup-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={path};Pooling=False;Default Timeout=1").Options;
            await using (var setup = new VocabularyDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                await setup.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", TestContext.Current.CancellationToken);
                await SeedAsync(setup);
            }
            await action(options);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix); }
    }
    private static VocabularyDomainService ImportService(VocabularyDbContext db) => new(new VocabularyRepository(db), new VocabularyBookRepository(db), new VocabularyMeaningRepository(db), new UnitOfWork(db));
    private static async Task ImportAsync(VocabularyDbContext db, string bookId) => await ImportService(db).AddOrUpdateAsync(new VocabularyModel { Word = "exclusive" }, new VocabularyMeaningModel { BookId = bookId, Meaning = "imported" });
    private sealed class ReadBarrier(TaskCompletionSource entered, TaskCompletionSource release) : DbCommandInterceptor
    {
        private bool _paused;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_paused)
            {
                _paused = true; entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
    private sealed class DeleteBarrier(TaskCompletionSource entered, TaskCompletionSource release) : DbCommandInterceptor
    {
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE FROM vocabulary_meaning ", StringComparison.Ordinal))
            { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            return result;
        }
    }
}
