using System.Data.Common;
using Lexarbor.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using static Lexarbor.Domain.Tests.VocabularyCleanupTests;

namespace Lexarbor.Domain.Tests;

public class VocabularyCleanupScaleTests
{
    [Fact]
    public async Task TwentyThousandFileRollbackWords_UseSetDeletesWithoutMaterializingMeanings()
    {
        await VocabularyCleanupConcurrencyTests.WithFileAsync(async options =>
        {
            var probe = new CommandProbe();
            await using var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>(options).AddInterceptors(probe).Options);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO vocabulary_book(id,book_name,status,display_order,created_at,updated_at) VALUES('large','Large',true,0,'2026-01-01','2026-01-01');
                INSERT INTO vocabulary(id,word,created_at,updated_at)
                SELECT 'w' || lpad(i::text,5,'0'),'w' || lpad(i::text,5,'0'),'2026-01-01','2026-01-01' FROM generate_series(1, 20000) AS n(i);
                INSERT INTO vocabulary_meaning(id,vocabulary_id,book_id,meaning,created_at,updated_at)
                SELECT id,id,'large','synthetic','2026-01-01','2026-01-01' FROM vocabulary WHERE id LIKE 'w%';
                """, TestContext.Current.CancellationToken);
            probe.Statements.Clear(); probe.Materialized = 0;
            var preview = await Service(db).PreviewAsync("large", new("clear"), TestContext.Current.CancellationToken);
            Assert.Equal((20000, 20000, 20000), (preview.AffectedWordCount, preview.MeaningCount, preview.OrphanWordCount));
            var result = await Service(db).CommitAsync("large", new("clear"), TestContext.Current.CancellationToken);
            Assert.Equal((20000, 20000, 20000), (result.AffectedWordCount, result.DeletedMeaningCount, result.DeletedWordCount));
            Assert.Equal(2, probe.Materialized); // book once per request; no words/meanings
            Assert.Equal(5, probe.Statements.Count); // create, insert candidates, two deletes, drop
            Assert.Contains(probe.Statements, s => s.Contains("INSERT INTO pg_temp.lexarbor_cleanup_affected SELECT DISTINCT"));
            Assert.Contains(probe.Statements, s => s.Contains("NOT EXISTS"));
            Assert.Equal(4, await db.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(5, await db.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
            Assert.True(await db.VocabularyBooks.AnyAsync(b => b.Id == "large", TestContext.Current.CancellationToken));
            await ForeignKeysAsync(db);
            Assert.Equal(0, await TempTableCountAsync(db));
            TestContext.Current.TestOutputHelper!.WriteLine(string.Join("\n\n", probe.Statements));
            TestContext.Current.TestOutputHelper.WriteLine("20000 meanings and 20000 affected orphan words deleted; two book entities materialized; FK check empty.");
        });
    }

    private sealed class CommandProbe : DbCommandInterceptor, IMaterializationInterceptor
    {
        public int Materialized;
        public List<string> Statements { get; } = [];
        public object InitializedInstance(MaterializationInterceptionData data, object entity) { Materialized++; return entity; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            // The preview's read-only snapshot marker is not a data statement.
            if (command.CommandText != "SET TRANSACTION READ ONLY") Statements.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
