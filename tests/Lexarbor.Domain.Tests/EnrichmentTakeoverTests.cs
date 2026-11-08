using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class EnrichmentTakeoverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Destination_ConcurrentStartupAndRestart_CreateOnce_DisabledIsPreserved_DeletionCanRecover()
    {
        using var file = new AudioTestDatabase();
        await using (var db = file.Open()) await db.Database.MigrateAsync(Ct);
        async Task<string?> Ensure()
        {
            await using var db = file.Open();
            return (await new EnrichmentDestinationRepository(db).EnsureAsync("reset-removed-book", 1000, Ct)).BookId;
        }
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Ensure()));
        Assert.Single(ids.Distinct()); var id = ids[0]!;
        await using var verify = file.Open();
        Assert.Equal(1, await verify.VocabularyBooks.CountAsync(Ct));
        await verify.VocabularyBooks.Where(b => b.Id == id).ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, false), Ct);
        var repo = new EnrichmentDestinationRepository(verify);
        Assert.Equal("Disabled", (await repo.EnsureAsync(null, 2000, Ct)).State);
        Assert.Equal(1, await verify.VocabularyBooks.CountAsync(Ct));
        await verify.VocabularyBooks.Where(b => b.Id == id).ExecuteDeleteAsync(Ct);
        var replacement = await repo.EnsureAsync(null, 3000, Ct);
        Assert.Equal("Ready", replacement.State); Assert.NotEqual(id, replacement.BookId);
        Assert.Equal(1, await verify.VocabularyBooks.CountAsync(Ct));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task Destination_ReadOnlyReportingDoesNotCreate_AndExplicitBookWinsEvenWhenDisabled()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await db.Database.MigrateAsync(Ct);
        var repo = new EnrichmentDestinationRepository(db);
        Assert.Equal("Missing", (await repo.ReadAsync("missing", Ct)).State);
        Assert.Empty(await db.EnrichmentDestinations.ToArrayAsync(Ct));
        db.VocabularyBooks.Add(new() { Id = "chosen", BookName = "Chosen", Status = false }); await db.SaveChangesAsync(Ct);
        var selected = await repo.EnsureAsync("chosen", 1000, Ct);
        Assert.Equal("Disabled", selected.State); Assert.False(selected.Managed);
        Assert.Empty(await db.EnrichmentDestinations.ToArrayAsync(Ct));
        await db.VocabularyBooks.ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, true), Ct);
        Assert.Equal("chosen", (await repo.EnsureAsync("chosen", 2000, Ct)).BookId);
    }

    [Fact]
    public async Task OldDatabase_UpgradeResumesHistoricalCursor_AndDiscoversEveryPageAcrossRestarts()
    {
        using var file = new AudioTestDatabase();
        await using (var db = file.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261102063605_AddMeaningSubmissionReviewChecks", Ct);
            await AudioTestDatabase.SeedAsync(db);
            await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET phonetic_uk=NULL,phonetic_us=NULL", Ct);
            await db.Database.MigrateAsync(Ct);
            // Simulate an interrupted first sweep, then reopen the context for each page.
            await new EnrichmentSchedulerRepository(db).DiscoverAsync(1, [], Ct);
        }
        for (var i = 0; i < 4; i++)
        {
            await using var db = file.Open();
            var repo = new EnrichmentSchedulerRepository(db);
            var found = await repo.DiscoverAsync(1, [], Ct);
            foreach (var key in found) await new EnrichmentTaskRepository(db).EnsureAsync(key, "fixture", 1000, Ct);
        }
        await using var final = file.Open();
        var progress = (await new EnrichmentSchedulerRepository(final).DiscoveryProgressAsync(Ct))!;
        Assert.True(progress.InitialCompleted); Assert.Equal(2, progress.WordsScanned); Assert.Equal(2, progress.MeaningsScanned);
        Assert.Equal(4, await final.EnrichmentTargets.CountAsync(t => t.Step == EnrichmentStep.Fields, Ct));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task TwoThousandLegacyWords_BoundedPagesFindMiddleGap_AndProgressSurvivesEveryRestart()
    {
        using var file = new AudioTestDatabase();
        await using (var seed = file.Open())
        {
            await seed.Database.MigrateAsync(Ct); await AudioTestDatabase.SeedAsync(seed);
            await seed.Database.ExecuteSqlRawAsync("""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<2000)
                INSERT INTO vocabulary(id,word,phonetic_uk,phonetic_us,created_at,updated_at)
                SELECT printf('legacy-%04d',i),printf('legacy-%04d',i),CASE WHEN i!=1000 THEN 'x' END,'x','2026-01-01','2026-01-01' FROM n;
                INSERT INTO vocabulary_meaning(id,vocabulary_id,book_id,part_of_speech,meaning,created_at,updated_at)
                SELECT id||'-sense',id,'book','n.','synthetic','2026-01-01','2026-01-01' FROM vocabulary WHERE id LIKE 'legacy-%';
                """, Ct);
        }
        var completed = false; var gapFound = false;
        for (var page = 0; page < 110 && !completed; page++)
        {
            await using var db = file.Open(); var scheduler = new EnrichmentSchedulerRepository(db);
            var targets = await scheduler.DiscoverAsync(20, [], Ct);
            gapFound |= targets.Any(t => t.WordId == "legacy-1000" && t.Step == EnrichmentStep.Fields);
            var progress = (await scheduler.DiscoveryProgressAsync(Ct))!;
            Assert.InRange(progress.WordsScanned, 0, 2002);
            completed = progress.InitialCompleted;
            if (completed) { Assert.Equal(2002, progress.WordsScanned); Assert.Equal(2002, progress.MeaningsScanned); }
        }
        Assert.True(completed); Assert.True(gapFound);
    }

    [Fact]
    public async Task Rc3Cursor_UpgradeBackfillsProgress_AndDownRetainsManagedBookAndOriginalCursor()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261108025122_AddAutomaticEnrichmentRecovery";
        await migrator.MigrateAsync(previous, Ct); await AudioTestDatabase.SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO enrichment_discovery (id,cursor,upper_bound) VALUES ('words','other','word'),('meanings','meaning','other-meaning')", Ct);
        await db.Database.MigrateAsync(Ct);
        var progress = (await new EnrichmentSchedulerRepository(db).DiscoveryProgressAsync(Ct))!;
        Assert.Equal(1, progress.WordsScanned); Assert.Equal(2, progress.WordsTotal);
        Assert.Equal(1, progress.MeaningsScanned); Assert.False(progress.InitialCompleted);
        var book = (await new EnrichmentDestinationRepository(db).EnsureAsync(null, 1000, Ct)).BookId;
        await migrator.MigrateAsync(previous, Ct);
        Assert.True(await db.VocabularyBooks.AnyAsync(b => b.Id == book, Ct));
        var cursor = await file.RowsAsync("SELECT cursor FROM enrichment_discovery WHERE id='words'");
        Assert.Equal("other", ((object?[])Assert.Single(cursor)!)[0]);
        await db.Database.MigrateAsync(Ct);
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task GlobalExpansionPause_ExcludesAllSourcesFromScheduling_AndRepeatedWaitsAreCoalesced()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await db.Database.MigrateAsync(Ct); await AudioTestDatabase.SeedAsync(db);
        var tasks = new EnrichmentTaskRepository(db);
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var task = (await tasks.EnsureAsync(new("word", "meaning", null, EnrichmentStep.Expand), "fixture", started, Ct))!;
        var fields = (await tasks.EnsureAsync(new("other", null, AudioLocale.EnGb, EnrichmentStep.Fields), "fixture", started, Ct))!;
        var scheduler = new EnrichmentSchedulerRepository(db);
        for (var i = 0; i < 5; i++)
        {
            var now = started + i * 30000;
            var guard = (await tasks.ClaimAsync(task.Id, "fixture", now, now + 10000, Ct))!;
            Assert.True(await tasks.WaitAsync(guard, EnrichmentWaitReason.ConfigurationMissing, now, now + 30000, Ct));
            Assert.DoesNotContain(await scheduler.PlanAsync(20, now + 30000, [], Ct, false), d => d.Step == "Expand");
        }
        Assert.Equal(1, await db.EnrichmentWaits.CountAsync(Ct));
        var ready = await scheduler.PlanAsync(20, started + 200000, [], Ct, true);
        Assert.Contains(ready, d => d.Id == task.Id); Assert.Contains(ready, d => d.Id == fields.Id);
        Assert.Empty(await db.EnrichmentCalls.ToArrayAsync(Ct));
    }
}
