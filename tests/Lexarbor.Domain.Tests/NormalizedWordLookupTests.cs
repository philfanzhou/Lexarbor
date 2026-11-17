using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// Covers the generated <c>normalized_word</c> column: that the lookup it exists
/// for is answered by its index, that it resolves a stored word written with
/// surrounding spaces and mixed case, and that the raw-SQL question queries
/// still materialize against the migrated table.
/// </summary>
public sealed class NormalizedWordLookupTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = new();

    [Fact]
    public async Task GetByNormalizedWordAsync_IsAnsweredByAnIndexSeek()
    {
        var capture = new CommandCapture();
        await using var context = _database.Open(capture);

        await new VocabularyRepository(context).GetByNormalizedWordAsync("apple");

        Assert.NotNull(capture.CommandText);
        var plan = await PostgreSqlPlan.ExplainAsync(_database.ConnectionString, capture.CommandText, capture.Parameters);

        // A predicate written as lower(trim(word)), a function around the
        // column, can only be answered by reading every row: the cost of
        // resolving one imported word would grow with the size of the
        // vocabulary, and it is paid while the process-wide write lock is held.
        Assert.Contains("IX_vocabulary_normalized_word", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan on vocabulary", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredWordWithSpacesAndMixedCase_ResolvesThroughTheGeneratedColumn()
    {
        await InsertWordAsync(_database.ConnectionString, "v-legacy", "  MIXEDcase  ");

        await using var context = CreateContext(_database.ConnectionString);
        var resolved = await new VocabularyRepository(context)
            .GetByNormalizedWordAsync("mixedcase");

        Assert.NotNull(resolved);
        Assert.Equal("v-legacy", resolved.Id);
    }

    [Fact]
    public async Task MigratedDatabase_StillServesTheRawSqlQuestionQueries()
    {
        await using var context = CreateContext(_database.ConnectionString);

        var bookRepository = new VocabularyBookRepository(context);
        var unitOfWork = new UnitOfWork(context);
        var service = new VocabularyDomainService(
            new VocabularyRepository(context),
            bookRepository,
            new VocabularyMeaningRepository(context),
            new VocabularyBookUnitRepository(context),
            new VocabularyMeaningUnitRepository(context),
            unitOfWork);

        await bookRepository.AddAsync(new VocabularyBookModel
        {
            Id = "book",
            BookName = "Book",
            Status = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);

        var (correct, _) = await service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = "book", PartOfSpeech = "n.", Meaning = "苹果" });
        var distractors = new[]
        {
            ("banana", "香蕉"),
            ("cherry", "樱桃"),
            ("date", "枣")
        };
        foreach (var (word, meaning) in distractors)
        {
            await service.AddOrUpdateAsync(
                new VocabularyModel { Word = word },
                new VocabularyMeaningModel { BookId = "book", PartOfSpeech = "n.", Meaning = meaning });
        }

        // Both directions read through FromSqlInterpolated with SELECT v.*, which
        // must carry every mapped column, the insertion_order shadow included.
        foreach (var chineseToEnglish in new[] { true, false })
        {
            var question = await service.CreateQuestionAsync(
                correct.Id,
                "book",
                chineseToEnglish);

            Assert.Equal(4, question.Options.Count);
            Assert.Single(question.Options, option => option.IsCorrect);
        }
    }

    private static async Task InsertWordAsync(string connectionString, string id, string word)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO vocabulary (id, word, phonetic_uk, phonetic_us, created_at, updated_at) " +
            "VALUES (@id, @word, null, null, now(), now())";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("word", word);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static VocabularyDbContext CreateContext(string connectionString) =>
        new(PostgreSqlTestServer.Options(connectionString).Options);

    public void Dispose() => _database.Dispose();

    /// <summary>
    /// Keeps the SQL the provider actually sent, so the plan above is the plan for
    /// the repository's own query rather than for a copy of it written here.
    /// </summary>
    private sealed class CommandCapture : DbCommandInterceptor
    {
        public string? CommandText { get; private set; }

        public DbParameter[] Parameters { get; private set; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CommandText = command.CommandText;
            Parameters = command.Parameters.Cast<DbParameter>().ToArray();
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandText = command.CommandText;
            Parameters = command.Parameters.Cast<DbParameter>().ToArray();
            return ValueTask.FromResult(result);
        }
    }
}
