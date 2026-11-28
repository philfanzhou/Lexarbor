using System.Data.Common;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Tests.Shared;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class LearningQuestionQueryPlanTests
{
    [Fact]
    public async Task ChineseToEnglishChecksEquivalentAnswersByIndexedCandidateSpelling()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = new PostgreSqlTestDatabase();
        var capture = new ChoiceQueryCapture();
        await using var db = database.Open(capture);
        db.VocabularyBooks.Add(new() { Id = "book", BookName = "Book", Status = true });
        db.Vocabularies.Add(new() { Id = "target", Word = "went" });
        db.VocabularyMeanings.Add(new() { Id = "meaning", VocabularyId = "target", BookId = "book", Meaning = "去了" });
        foreach (var spelling in new[] { "go", "going", "gone" })
        {
            db.Vocabularies.Add(new() { Id = spelling, Word = spelling });
            db.VocabularyMeanings.Add(new() { Id = $"m-{spelling}", VocabularyId = spelling, BookId = "book", Meaning = spelling });
            db.VocabularyRelations.Add(new()
            {
                Id = spelling,
                SourceWordId = "target",
                TargetWordId = spelling,
                Type = "derived_verb",
                OriginKind = "manual",
                Status = "confirmed",
                ReviewReason = null,
                LastConfirmedAtUnixMs = 0
            });
        }
        await db.SaveChangesAsync(token);

        var options = await new VocabularyPublicQueryRepository(db).GetReviewDistractorsAsync(
            new Vocabulary { Id = "target", Word = "went" },
            new VocabularyMeaning { Id = "meaning", VocabularyId = "target", BookId = "book", Meaning = "去了" },
            null, chineseToEnglish: true, supplementary: false, token);
        Assert.NotNull(options);
        Assert.Equal(new[] { "go", "going", "gone" }, options.Select(option => option.Text).Order());

        var (sql, parameters) = Assert.Single(capture.Commands);
        var plan = await PostgreSqlPlan.ExplainAsync(database.ConnectionString, sql, parameters);
        Assert.Contains("IX_vocabulary_normalized_word", plan, StringComparison.Ordinal);
        Assert.Contains("IX_vocabulary_meaning_vocabulary_id", plan, StringComparison.Ordinal);
    }

    private sealed class ChoiceQueryCapture : DbCommandInterceptor
    {
        public List<(string Sql, DbParameter[] Parameters)> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("random()", StringComparison.OrdinalIgnoreCase))
                Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
