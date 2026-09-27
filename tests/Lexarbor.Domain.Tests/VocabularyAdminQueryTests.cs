using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public class VocabularyAdminQueryTests : TestBase
{
    private VocabularyAdminQueryService Service => new(new VocabularyAdminQueryRepository(_dbContext));

    [Fact]
    public async Task AllLibrary_ContentAndDetail_PreserveDisabledSharedAndOrphanData()
    {
        await SeedAsync(_dbContext);
        var before = await _dbContext.VocabularyMeanings.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        var all = await Service.SearchAsync(null, null, null, null, TestContext.Current.CancellationToken);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(new[] { "only-b", "orphan", "shared" }, all.Items.Select(i => i.Word.Id));
        Assert.Empty(all.Items.Single(i => i.Word.Id == "orphan").Books);
        var filtered = await Service.SearchAsync(null, "A", 1, 20, TestContext.Current.CancellationToken);
        Assert.Single(filtered.Items);
        Assert.Equal(new[] { "A", "B" }, filtered.Items[0].Books.Select(b => b.Id));
        Assert.False(filtered.Items[0].Books[1].Status);
        var detail = await Service.GetAsync("shared", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "a1", "a2", "b1" }, detail.Meanings.Select(m => m.Id));
        var content = await Service.GetContentAsync("A", " SHARED ", 1, 20, TestContext.Current.CancellationToken);
        Assert.Equal(1, content.WordCount);
        Assert.Equal(2, content.MeaningCount);
        Assert.Equal(1, content.Page.TotalCount);
        Assert.All(content.Page.Items.Single().Meanings, m => Assert.Equal("A", m.BookId));
        var noMatch = await Service.GetContentAsync("A", "no-match", 1, 20, TestContext.Current.CancellationToken);
        Assert.Equal(2, noMatch.MeaningCount);
        Assert.Equal(1, noMatch.WordCount);
        Assert.Equal(0, noMatch.Page.TotalPage);
        Assert.Empty(noMatch.Page.Items);
        Assert.Empty((await Service.GetAsync("orphan", TestContext.Current.CancellationToken)).Meanings);
        var empty = await Service.GetContentAsync("empty", null, 1, 20, TestContext.Current.CancellationToken);
        Assert.Equal(0, empty.WordCount);
        Assert.Equal(0, empty.MeaningCount);
        Assert.Equal(0, empty.Page.TotalPage);
        Assert.Empty(empty.Page.Items);
        Assert.Empty((await Service.SearchAsync(null, null, 100, 20, TestContext.Current.CancellationToken)).Items);
        Assert.Equal(before.Select(m => (m.Id, m.Meaning, m.BookId, m.UpdatedAt)),
            (await _dbContext.VocabularyMeanings.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)).Select(m => (m.Id, m.Meaning, m.BookId, m.UpdatedAt)));
    }

    [Theory]
    [InlineData(" % ", "percent%")]
    [InlineData("_", "under_score")]
    [InlineData("\\", "back\\slash")]
    [InlineData(" MIXED ", "mixed")]
    public async Task Keyword_IsTrimmedAsciiLikeWithLiteralWildcards(string keyword, string expected)
    {
        _dbContext.Vocabularies.AddRange(new[] { "percent%", "under_score", "back\\slash", "mixed", "other" }
            .Select(w => new VocabularyEntity { Id = w, Word = w }));
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, (await Service.SearchAsync(keyword, null, 0, 0, TestContext.Current.CancellationToken)).Items.Single().Word.Word);
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public async Task InvalidPaging_IsRejectedBeforeReading(int page, int size)
        => await Assert.ThrowsAsync<DomainValidationException>(() => Service.SearchAsync(null, null, page, size, TestContext.Current.CancellationToken));

    [Fact]
    public async Task MissingResourcesAndCancellation_DoNotWrite()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service.GetAsync("missing", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service.SearchAsync(null, "missing", null, null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service.GetContentAsync("missing", null, null, null, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.SearchAsync(null, null, null, null, cancelled.Token));
        Assert.Empty(await _dbContext.Vocabularies.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TwentyThousandWords_OnlyPageAssociationsMaterialized_IndexedAndNoNPlusOne()
    {
        var statements = new List<string>();
        var materialization = new MaterializationCounter();
        var capture = new SqlCapture(statements);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite(connection)
            .AddInterceptors(materialization, capture).Options;
        await using var context = new VocabularyDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO vocabulary_book(id,book_name,status,display_order,created_at,updated_at) VALUES('large','Large',1,0,'2026-01-01','2026-01-01');
            WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<20000)
            INSERT INTO vocabulary(id,word,created_at,updated_at) SELECT printf('w%05d',i),printf('w%05d',i),'2026-01-01','2026-01-01' FROM n;
            INSERT INTO vocabulary_meaning(id,vocabulary_id,book_id,meaning,created_at,updated_at)
            SELECT id,id,'large','synthetic','2026-01-01','2026-01-01' FROM vocabulary;
            """, TestContext.Current.CancellationToken);
        statements.Clear(); materialization.Count = 0;
        var service = new VocabularyAdminQueryService(new VocabularyAdminQueryRepository(context));
        var page = await service.GetContentAsync("large", "W", 1, 20, TestContext.Current.CancellationToken);
        Assert.Equal(20000, page.WordCount);
        Assert.Equal(20000, page.Page.TotalCount);
        Assert.Equal(20, page.Page.Items.Count);
        Assert.Equal(41, materialization.Count); // one book + 20 words + 20 meanings
        Assert.Equal(7, statements.Count);
        Assert.Contains(statements, sql => sql.Contains("LIMIT") && sql.Contains("OFFSET"));
        Assert.Equal(2, statements.Count(sql => sql.Contains("json_each") || sql.Contains(" IN (")));
        TestContext.Current.TestOutputHelper!.WriteLine(string.Join("\n\n", statements));
        await using var command = connection.CreateCommand();
        var captured = capture.Commands.Last(c => c.Sql.Contains("LIMIT") && c.Sql.Contains("OFFSET"));
        command.CommandText = "EXPLAIN QUERY PLAN " + captured.Sql;
        foreach (var (name, value) in captured.Parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var plans = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) plans.Add(reader.GetString(3));
        TestContext.Current.TestOutputHelper.WriteLine(string.Join("\n", plans));
        Assert.Contains(plans, p => p.Contains("COVERING INDEX IX_vocabulary_meaning_") && p.Contains("book_id=?") && p.Contains("vocabulary_id=?"));
        Assert.Contains(plans, p => p.Contains("IX_vocabulary_word"));
        TestContext.Current.TestOutputHelper.WriteLine(string.Join("\n", plans));
        var next = await service.SearchAsync(null, "large", 2, 20, TestContext.Current.CancellationToken);
        Assert.Empty(page.Page.Items.Select(i => i.Word.Id).Intersect(next.Items.Select(i => i.Word.Id)));
        Assert.Equal(100, (await service.SearchAsync(null, null, 1, 100, TestContext.Current.CancellationToken)).Items.Count);
    }

    [Fact]
    public async Task FileWal_ConcurrentWriterCommits_WhileResponseKeepsOneDeferredSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lexarbor-admin-query-{Guid.NewGuid():N}.db");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var options = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var writer = new VocabularyDbContext(options);
            await writer.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            await writer.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", TestContext.Current.CancellationToken);
            await SeedAsync(writer);
            var readOptions = new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={path};Pooling=False")
                .AddInterceptors(new ReadBarrier(entered, release)).Options;
            await using var reader = new VocabularyDbContext(readOptions);
            var query = new VocabularyAdminQueryService(new VocabularyAdminQueryRepository(reader))
                .GetContentAsync("A", null, 1, 20, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            try
            {
                await new UnitOfWork(writer).ExecuteInTransactionAsync(async () =>
                {
                    await writer.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning WHERE book_id='A'", TestContext.Current.CancellationToken);
                    return 0;
                }).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }
            finally { release.TrySetResult(); }
            var result = await query;
            Assert.Equal(2, result.MeaningCount);
            Assert.Equal(2, result.Page.Items.Single().Meanings.Count);
            Assert.Equal(0, await writer.VocabularyMeanings.CountAsync(m => m.BookId == "A", TestContext.Current.CancellationToken));
        }
        finally
        {
            release.TrySetResult();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    internal static async Task SeedAsync(VocabularyDbContext context)
    {
        context.VocabularyBooks.AddRange(new VocabularyBookEntity { Id = "A", BookName = "A", Status = true },
            new VocabularyBookEntity { Id = "B", BookName = "B", Status = false }, new VocabularyBookEntity { Id = "empty", BookName = "Empty" });
        context.Vocabularies.AddRange(new[] { "shared", "only-b", "orphan" }.Select(id => new VocabularyEntity { Id = id, Word = id }));
        context.VocabularyMeanings.AddRange(new[] { ("a1", "shared", "A", "a"), ("a2", "shared", "A", "b"), ("b1", "shared", "B", "c"), ("b2", "only-b", "B", "d") }
            .Select(x => new VocabularyMeaningEntity { Id = x.Item1, VocabularyId = x.Item2, BookId = x.Item3, Meaning = x.Item4 }));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private sealed class MaterializationCounter : IMaterializationInterceptor
    {
        public int Count;
        public object InitializedInstance(MaterializationInterceptionData data, object entity) { Count++; return entity; }
    }
    private sealed class SqlCapture(List<string> statements) : DbCommandInterceptor
    {
        public List<(string Sql, (string Name, object Value)[] Parameters)> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { statements.Add(command.CommandText); Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value!)).ToArray())); return ValueTask.FromResult(result); }
    }
    private sealed class ReadBarrier(TaskCompletionSource entered, TaskCompletionSource release) : DbCommandInterceptor
    {
        private bool _paused;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_paused)
            {
                _paused = true;
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
