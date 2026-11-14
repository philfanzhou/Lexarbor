using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class LearningQuestionQueryPlanTests
{
    [Fact]
    public async Task ChineseToEnglishChecksEquivalentAnswersByIndexedCandidateSpelling()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        var capture = new ChoiceQueryCapture();
        await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseSqlite(connection).AddInterceptors(capture).Options);
        await db.Database.EnsureCreatedAsync(token);
        db.VocabularyBooks.Add(new() { Id = "book", BookName = "Book", Status = true });
        db.Vocabularies.Add(new() { Id = "target", Word = "went" });
        db.VocabularyMeanings.Add(new() { Id = "meaning", VocabularyId = "target", BookId = "book", Meaning = "去了" });
        foreach (var spelling in new[] { "go", "going", "gone" })
        {
            db.Vocabularies.Add(new() { Id = spelling, Word = spelling });
            db.VocabularyMeanings.Add(new() { Id = $"m-{spelling}", VocabularyId = spelling, BookId = "book", Meaning = spelling });
            db.VocabularyRelations.Add(new()
            {
                Id = spelling, SourceWordId = "target", TargetWordId = spelling, Type = "derived_verb",
                OriginKind = "manual", Status = "confirmed", ReviewReason = null, LastConfirmedAtUnixMs = 0
            });
        }
        await db.SaveChangesAsync(token);

        var options = await new VocabularyPublicQueryRepository(db).GetReviewDistractorsAsync(
            new VocabularyModel { Id = "target", Word = "went" },
            new VocabularyMeaningModel { Id = "meaning", VocabularyId = "target", BookId = "book", Meaning = "去了" },
            null, chineseToEnglish: true, supplementary: false, token);
        Assert.NotNull(options);
        Assert.Equal(new[] { "go", "going", "gone" }, options.Select(option => option.Text).Order());

        var (sql, parameters) = Assert.Single(capture.Commands);
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(token);
        var plan = new List<string>();
        while (await reader.ReadAsync(token)) plan.Add(reader.GetString(3));
        Assert.Contains(plan, step => step.Contains("CORRELATED SCALAR SUBQUERY", StringComparison.Ordinal));
        Assert.Contains(plan, step => step.Contains("USING INDEX IX_vocabulary_normalized_word (normalized_word=?)", StringComparison.Ordinal));
        Assert.Contains(plan, step => step.Contains("IX_vocabulary_meaning_vocabulary_id (vocabulary_id=?)", StringComparison.Ordinal));
    }

    private sealed class ChoiceQueryCapture : DbCommandInterceptor
    {
        public List<(string Sql, SqliteParameter[] Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("random()", StringComparison.OrdinalIgnoreCase))
                Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>()
                    .Select(parameter => new SqliteParameter(parameter.ParameterName, parameter.Value)).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
