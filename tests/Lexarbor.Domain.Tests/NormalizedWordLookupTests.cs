using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// Covers the generated <c>normalized_word</c> column: that the lookup it exists
/// for is answered by its index, that adding it to a database which already holds
/// rows works, and that the raw-SQL question queries still materialize against the
/// column order a migrated table ends up with.
/// </summary>
public sealed class NormalizedWordLookupTests : IDisposable
{
    private const string InitialCreateMigration = "20260805134233_InitialCreate";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"lexarbor-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetByNormalizedWordAsync_IsAnsweredByAnIndexSeek()
    {
        var capture = new CommandCapture();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new VocabularyDbContext(
            new DbContextOptionsBuilder<VocabularyDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(capture)
                .Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        await new VocabularyRepository(context).GetByNormalizedWordAsync("apple");

        Assert.NotNull(capture.CommandText);
        var plan = Explain(connection, capture.CommandText);

        // The predicate this replaced read lower(trim(word)), a function around
        // the column, which SQLite can only answer by reading every row: the cost
        // of resolving one imported word grew with the size of the vocabulary,
        // and it is paid while the process-wide write lock is held.
        Assert.Contains("USING INDEX IX_vocabulary_normalized_word", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN vocabulary", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_AddsTheColumnToADatabaseThatAlreadyHasRows()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "upgraded.db");

        // SQLite refuses ALTER TABLE ADD COLUMN for a stored generated column, so
        // an existing deployment is the case this migration has to survive; a
        // schema built from scratch would never exercise it.
        await using (var context = CreateContext(databasePath))
        {
            context.GetService<IMigrator>().Migrate(InitialCreateMigration);
        }

        await InsertLegacyWordAsync(databasePath, "v-legacy", "  MIXEDcase  ");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            var resolved = await new VocabularyRepository(context)
                .GetByNormalizedWordAsync("mixedcase");

            Assert.NotNull(resolved);
            Assert.Equal("v-legacy", resolved.Id);
        }
    }

    [Fact]
    public async Task MigratedDatabase_StillServesTheRawSqlQuestionQueries()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var databasePath = Path.Combine(_temporaryDirectory, "questions.db");

        await using var context = CreateContext(databasePath);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var bookRepository = new VocabularyBookRepository(context);
        var unitOfWork = new UnitOfWork(context);
        var service = new VocabularyDomainService(
            new VocabularyRepository(context),
            bookRepository,
            new VocabularyMeaningRepository(context),
            unitOfWork);

        await bookRepository.AddAsync(new VocabularyBookModel
        {
            Id = "book",
            BookName = "Book",
            Status = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await unitOfWork.SaveChangesAsync();

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

        // Both directions read through FromSqlInterpolated with SELECT v.*, and a
        // migrated table carries the new column in a different position from one
        // the model created outright. Nothing else in the suite runs those
        // queries against the migrated shape.
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

    private static async Task InsertLegacyWordAsync(string databasePath, string id, string word)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO vocabulary (id, word, phonetic_uk, phonetic_us, created_at, updated_at) " +
            "VALUES ($id, $word, null, null, $now, $now)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$word", word);
        command.Parameters.AddWithValue("$now", "2026-01-01 00:00:00.0000000+00:00");
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string Explain(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read())
        {
            plan.Add(reader.GetString(3));
        }

        return string.Join(Environment.NewLine, plan);
    }

    private static VocabularyDbContext CreateContext(string databasePath)
    {
        return new VocabularyDbContext(
            new DbContextOptionsBuilder<VocabularyDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options);
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

    /// <summary>
    /// Keeps the SQL the provider actually sent, so the plan above is the plan for
    /// the repository's own query rather than for a copy of it written here.
    /// </summary>
    private sealed class CommandCapture : DbCommandInterceptor
    {
        public string? CommandText { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CommandText = command.CommandText;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CommandText = command.CommandText;
            return ValueTask.FromResult(result);
        }
    }
}
