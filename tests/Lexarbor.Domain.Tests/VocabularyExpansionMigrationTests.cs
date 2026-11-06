using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class VocabularyExpansionMigrationTests
{
    private const string Previous = "20261105011010_AddVocabularyRelations";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpgradeAndDown_PreservePronunciationHistoryCatalogAndDeletionTriggers()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous, Ct); await AudioTestDatabase.SeedAsync(db);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var tasks = new EnrichmentTaskRepository(db);
        var audio = (await tasks.EnsureAsync(new("word", null, AudioLocale.EnGb, EnrichmentStep.Audio), "old", now, Ct))!;
        var guard = (await tasks.ClaimAsync(audio.Id, "before", now, now + 60_000, Ct))!;
        var call = Guid.NewGuid().ToString();
        await tasks.BeginCallAsync(guard, call, "tts", null, now, Ct);
        await tasks.FinishCallAsync(guard, call, EnrichmentCallResult.TerminalFailure, "tts.fixture", now, null, Ct);
        var before = (await tasks.GetAsync(audio.Id, Ct))!;
        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.False(db.Database.HasPendingModelChanges());
        var preserved = (await tasks.GetAsync(audio.Id, Ct))!;
        Assert.Equal(before.Rounds, preserved.Rounds); Assert.Equal(before.Calls, preserved.Calls);
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
        var expansion = (await tasks.EnsureAsync(new("word", "meaning", null, EnrichmentStep.Expand), "text", now, Ct))!;
        Assert.Null(expansion.Target.Locale);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE enrichment_target SET locale='en-GB' WHERE step='Expand'", Ct));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO enrichment_target SELECT 'duplicate',word_id,meaning_id,locale,step,current_round,epoch,deleted FROM enrichment_target WHERE step='Expand'", Ct));
        await migrator.MigrateAsync(Previous, Ct);
        preserved = (await tasks.GetAsync(audio.Id, Ct))!;
        Assert.Equal(before.Rounds, preserved.Rounds); Assert.Equal(before.Calls, preserved.Calls);
        Assert.Null(await tasks.GetAsync(expansion.Id, Ct));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
        await migrator.MigrateAsync(cancellationToken: Ct);
        var requeued = (await tasks.EnsureAsync(new("word", "meaning", null, EnrichmentStep.Expand), "text", now, Ct))!;
        await db.VocabularyMeanings.ExecuteDeleteAsync(Ct);
        var cancelled = (await tasks.GetAsync(requeued.Id, Ct))!;
        Assert.True(cancelled.Deleted); Assert.Equal(EnrichmentState.Cancelled, cancelled.Rounds.Last().State);
        Assert.Equal(2, await db.Vocabularies.CountAsync(Ct));
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
    }
}
