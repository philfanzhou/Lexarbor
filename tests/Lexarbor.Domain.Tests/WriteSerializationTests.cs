using System.Diagnostics;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// PostgreSQL write coordination and the application contract for competing transactions.
/// </summary>
public sealed class WriteSerializationTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = new();

    [Fact]
    public async Task ConcurrentEquivalentImports_AreIdempotent()
    {
        var databasePath = _database.ConnectionString;
        await using (var setupContext = CreateContext(databasePath))
        {
            await setupContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            setupContext.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "concurrency-book",
                BookName = "Concurrency Book",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var firstContext = CreateContext(databasePath);
        await using var secondContext = CreateContext(databasePath);
        var firstService = CreateService(firstContext);
        var secondService = CreateService(secondContext);

        var results = await Task.WhenAll(
            firstService.AddOrUpdateAsync(
                new Vocabulary { Word = " Apple " },
                new VocabularyMeaning
                {
                    BookId = "concurrency-book",
                    PartOfSpeech = " N. ",
                    Meaning = " fruit "
                }),
            secondService.AddOrUpdateAsync(
                new Vocabulary { Word = "APPLE" },
                new VocabularyMeaning
                {
                    BookId = "concurrency-book",
                    PartOfSpeech = "n.",
                    Meaning = "fruit"
                }));

        Assert.Equal(results[0].word.Id, results[1].word.Id);
        Assert.Equal(results[0].meaning.Id, results[1].meaning.Id);

        await using var verificationContext = CreateContext(databasePath);
        Assert.Equal(1, await verificationContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    // Scenario 8 of the batch import model (#72, ADR-005): two identical
    // batches from separate requests are serialized by the write lock, so one
    // creates everything and the other matches all of it.
    [Fact]
    public async Task ConcurrentIdenticalBatches_AreSerializedAndIdempotent()
    {
        var databasePath = _database.ConnectionString;
        await using (var setupContext = CreateContext(databasePath))
        {
            await setupContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            setupContext.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "batch-book",
                BookName = "Batch Book",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        const int entryCount = 50;
        static List<(Vocabulary, VocabularyMeaning, string?, string?, string?)> Batch() =>
            Enumerable.Range(0, entryCount)
                .Select(index => (
                    new Vocabulary { Word = $"word{index:D2}" },
                    new VocabularyMeaning { PartOfSpeech = "n.", Meaning = $"meaning {index:D2}" },
                    (string?)null, (string?)null, (string?)null))
                .ToList();

        await using var firstContext = CreateContext(databasePath);
        await using var secondContext = CreateContext(databasePath);
        var results = await Task.WhenAll(
            CreateService(firstContext).ImportBatchAsync("batch-book", Batch()),
            CreateService(secondContext).ImportBatchAsync("batch-book", Batch()));

        Assert.Contains(new VocabularyBatchImportResult(entryCount, entryCount, 0), results);
        Assert.Contains(new VocabularyBatchImportResult(entryCount, 0, entryCount), results);

        await using var verificationContext = CreateContext(databasePath);
        Assert.Equal(entryCount, await verificationContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(entryCount, await verificationContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BookWrite_WhileAnotherConnectionHoldsTheDatabase_ReportsStorageBusy()
    {
        var databasePath = _database.ConnectionString;
        await using (var setupContext = CreateContext(databasePath))
        {
            await setupContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            setupContext.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "busy-book",
                BookName = "Busy Book",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Hold a real PostgreSQL row lock until the other connection hits lock_timeout.
        await using var blockingContext = CreateContext(databasePath);
        await blockingContext.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await blockingContext.Database.ExecuteSqlRawAsync(
            "BEGIN; SELECT 1 FROM vocabulary_book WHERE id = 'busy-book' FOR UPDATE;", TestContext.Current.CancellationToken);

        try
        {
            await using var context = CreateContext(databasePath, timeoutSeconds: 1);
            var bookRepository = new VocabularyBookRepository(context);
            var service = new VocabularyBookService(bookRepository, new UnitOfWork(context));
            var book = await service.GetAsync("busy-book");
            Assert.NotNull(book);
            book.BookName = "Renamed";

            var stopwatch = Stopwatch.StartNew();
            // Previously this surfaced as a raw DbUpdateException, which the
            // exception middleware has no case for, so an ordinary rename
            // answered 500 "An unexpected error occurred." after the driver had
            // spent its full default timeout retrying.
            await Assert.ThrowsAsync<StorageBusyException>(() => service.AddOrUpdateAsync(book));
            stopwatch.Stop();

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"The write should fail within its configured timeout, took {stopwatch.Elapsed}.");
        }
        finally
        {
            await blockingContext.Database.ExecuteSqlRawAsync(
                "ROLLBACK;", TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task BookWrite_CanCommitBesideAnUnrelatedWordWrite()
    {
        var databasePath = _database.ConnectionString;
        await using (var setupContext = CreateContext(databasePath))
        {
            await setupContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            setupContext.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "serialized-book",
                BookName = "Serialized Book",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var importContext = CreateContext(databasePath, timeoutSeconds: 1);
        await using var bookContext = CreateContext(databasePath, timeoutSeconds: 1);

        // An import transaction, held open on demand so the race is a fact of
        // the test rather than a matter of timing.
        var importIsWriting = new TaskCompletionSource();
        var releaseImport = new TaskCompletionSource();
        var import = new UnitOfWork(importContext).ExecuteInTransactionAsync(async () =>
        {
            // A row the book write does not own, so both effects can be
            // asserted afterwards. Touching a column of the book itself would
            // only show which replace ran last.
            await importContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO vocabulary (id, word, created_at, updated_at)
                VALUES ('imported-word', 'imported', '2026-01-01T00:00:00+00:00',
                        '2026-01-01T00:00:00+00:00');
                """);
            importIsWriting.SetResult();
            await releaseImport.Task;
            return 0;
        });
        await importIsWriting.Task;

        var service = new VocabularyBookService(
            new VocabularyBookRepository(bookContext),
            new UnitOfWork(bookContext));
        var book = await service.GetAsync("serialized-book");
        Assert.NotNull(book);
        book.Description = "Renamed while an import was in flight";
        var bookWrite = service.AddOrUpdateAsync(book);

        try
        {
            await bookWrite.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseImport.TrySetResult();
            await Task.WhenAny(
                import,
                Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            await Task.WhenAny(
                bookWrite,
                Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        }

        await import;
        await bookWrite;

        await using var verificationContext = CreateContext(databasePath);
        var stored = await verificationContext.VocabularyBooks.SingleAsync(
            item => item.Id == "serialized-book", TestContext.Current.CancellationToken);
        Assert.Equal("Renamed while an import was in flight", stored.Description);
        Assert.True(await verificationContext.Vocabularies.AnyAsync(
            item => item.Id == "imported-word", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IndependentWords_InTheSameBook_CanCommitConcurrently()
    {
        var token = TestContext.Current.CancellationToken;
        await using var first = _database.Open();
        await using var second = _database.Open();
        first.VocabularyBooks.Add(new() { Id = "book", BookName = "Book", Status = true });
        await first.SaveChangesAsync(token);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new UnitOfWork(first).ExecuteInTransactionAsync(async () =>
        {
            var imported = await CreateService(first).AddOrUpdateAsync(new() { Word = "apple" },
                new() { BookId = "book", Meaning = "fruit" });
            entered.SetResult();
            await release.Task;
            return imported;
        }, token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await CreateService(second).AddOrUpdateAsync(new() { Word = "birch" },
                new() { BookId = "book", Meaning = "tree" }).WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(new[] { "birch" }, await second.Vocabularies.Select(word => word.Word).ToArrayAsync(token));
        }
        finally
        {
            release.TrySetResult();
            await held;
        }
        Assert.Equal(2, await second.VocabularyMeanings.CountAsync(token));
    }

    [Fact]
    public async Task SerializationFailure_RollsBackAndReportsConflictWithoutReplayingTheCallback()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = _database.Open();
        var attempts = 0;
        await Assert.ThrowsAsync<ConflictException>(() => new UnitOfWork(db).ExecuteInTransactionAsync(async () =>
        {
            attempts++;
            db.VocabularyBooks.Add(new() { Id = "conflict", BookName = "Conflict" });
            await db.SaveChangesAsync(token);
            return await db.Database.ExecuteSqlRawAsync(
                "DO $$ BEGIN RAISE serialization_failure; END $$", token);
        }, token));
        Assert.Equal(1, attempts);
        Assert.Empty(await db.VocabularyBooks.AsNoTracking().ToListAsync(token));
    }

    [Fact]
    public async Task ImportWrite_NestsSaveChangesInsideItsTransaction_WithoutDeadlocking()
    {
        var databasePath = _database.ConnectionString;
        await using var context = CreateContext(databasePath);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        context.VocabularyBooks.Add(new VocabularyBookEntity
        {
            Id = "reentrancy-book",
            BookName = "Reentrancy Book",
            Status = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // SaveChanges must join the transaction already owned by this context.
        var import = CreateService(context).AddOrUpdateAsync(
            new Vocabulary { Word = "apple" },
            new VocabularyMeaning { BookId = "reentrancy-book", Meaning = "fruit" });
        var timeout = Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Same(import, await Task.WhenAny(import, timeout));
        var (word, meaning) = await import;
        Assert.Equal("apple", word.Word);
        Assert.Equal("fruit", meaning.Meaning);
    }

    public void Dispose() => _database.Dispose();

    private static VocabularyService CreateService(VocabularyDbContext context)
    {
        return new VocabularyService(
            new VocabularyRepository(context),
            new VocabularyBookRepository(context),
            new VocabularyMeaningRepository(context),
            new VocabularyBookUnitRepository(context),
            new VocabularyMeaningUnitRepository(context),
            new UnitOfWork(context));
    }

    private static VocabularyDbContext CreateContext(
        string connectionString,
        int? timeoutSeconds = null)
    {
        if (timeoutSeconds != null)
        {
            connectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Options = $"-c lock_timeout={timeoutSeconds}s"
            }.ConnectionString;
        }

        return new VocabularyDbContext(PostgreSqlTestServer.Options(connectionString).Options);
    }
}
