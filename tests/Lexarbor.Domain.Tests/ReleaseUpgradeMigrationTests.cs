using System.Data.Common;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class ReleaseUpgradeMigrationTests
{
    // v0.1.8, commit 55ab685939b065eaa4b8940a7f96ceb78bdd3a5b.
    // All historical migration sources through this boundary match that tag.
    private const string Release = "20261102063605_AddMeaningSubmissionReviewChecks";
    private const string Expansion = "20261105152609_AddVocabularyExpansion";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FromV018_AllPersistedTablesSurviveUpgradeAndRepeatedStartup()
    {
        using var file = new AudioTestDatabase();
        await SeedRelease(file);
        var before = await Snapshot(file);
        Assert.Equal(24, before.Count);
        foreach (var rows in before.Values) Assert.NotEmpty(rows);
        var history = await file.RowsAsync("SELECT * FROM __EFMigrationsHistory ORDER BY MigrationId");
        await using (var db = file.Open()) await db.Database.MigrateAsync(Ct);
        await AssertPreserved(file, before);
        var upgraded = await file.RowsAsync("SELECT * FROM __EFMigrationsHistory ORDER BY MigrationId");
        Assert.Equal(JsonSerializer.Serialize(history), JsonSerializer.Serialize(upgraded.Take(history.Length)));
        Assert.Equal(history.Length + 4, upgraded.Length);
        await using (var restarted = file.Open()) await restarted.Database.MigrateAsync(Ct);
        await AssertPreserved(file, before);
        Assert.Equal(JsonSerializer.Serialize(upgraded), JsonSerializer.Serialize(await file.RowsAsync("SELECT * FROM __EFMigrationsHistory ORDER BY MigrationId")));
        Assert.Empty(await file.RowsAsync("SELECT * FROM enrichment_generated_word"));
        Assert.Empty(await file.RowsAsync("SELECT * FROM vocabulary_relation"));
    }

    [Fact]
    public async Task PreparationPendingMigration_DownConvertsDependencyWaitAndPreservesHistory()
    {
        using var file = new AudioTestDatabase();
        await SeedRelease(file);
        await using (var db = file.Open()) await db.Database.MigrateAsync(Ct);
        await using (var db = file.Open())
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE enrichment_round SET wait_reason='PreparationPending' WHERE target_id='task' AND number=2;
                UPDATE enrichment_wait SET reason='PreparationPending' WHERE id='wait';
                """, Ct);
        await using (var db = file.Open())
            await db.GetService<IMigrator>().MigrateAsync("20261106071759_AiRequestBatching", Ct);
        var round = (object?[])Assert.Single(await file.RowsAsync("SELECT wait_reason FROM enrichment_round WHERE target_id='task' AND number=2"))!;
        var wait = (object?[])Assert.Single(await file.RowsAsync("SELECT reason FROM enrichment_wait WHERE id='wait'"))!;
        Assert.Equal("ConfigurationMissing", (string)round[0]!);
        Assert.Equal("ConfigurationMissing", (string)wait[0]!);
        Assert.Equal(2, (await file.RowsAsync("SELECT id FROM enrichment_call ORDER BY id")).Length);
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
        Assert.Equal("ok", Assert.IsType<object?[]>(Assert.Single(await file.RowsAsync("PRAGMA integrity_check")))[0]);
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX IX_enrichment_target_word_id_meaning_id ON")]
    [InlineData("CREATE TRIGGER enrichment_word_deleted")]
    [InlineData("INSERT INTO \"__EFMigrationsHistory\"")]
    public async Task InterruptedExpansion_RollsBackSchemaDataAndHistory_ThenResumes(string point)
    {
        using var file = new AudioTestDatabase();
        await SeedRelease(file);
        var before = await Snapshot(file);
        var targetSchema = await file.RowsAsync("SELECT sql FROM sqlite_master WHERE name='enrichment_target'");
        var fault = new MigrationFault(point);
        await using (var db = file.Open(fault))
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.MigrateAsync(Ct));
        Assert.True(fault.Triggered);
        await AssertPreserved(file, before);
        Assert.Equal(JsonSerializer.Serialize(targetSchema), JsonSerializer.Serialize(await file.RowsAsync("SELECT sql FROM sqlite_master WHERE name='enrichment_target'")));
        Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name LIKE 'expansion_%' OR name='enrichment_generated_word'"));
        Assert.Empty(await file.RowsAsync($"SELECT * FROM __EFMigrationsHistory WHERE MigrationId='{Expansion}'"));
        await using (var resumed = file.Open()) await resumed.Database.MigrateAsync(Ct);
        await AssertPreserved(file, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FromRc2_BatchingIsAdditiveAndCrashSafe(bool interrupt)
    {
        using var file = new AudioTestDatabase();
        await SeedRelease(file);
        await using (var db = file.Open())
        {
            await db.GetService<IMigrator>().MigrateAsync(Expansion, Ct);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO enrichment_generated_word VALUES ('word','existing-expansion-task');", Ct);
        }
        var before = await Snapshot(file);
        if (interrupt)
        {
            var fault = new MigrationFault("CREATE TABLE \"ai_request_batch_member\"");
            await using var db = file.Open(fault);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.MigrateAsync(Ct));
            Assert.True(fault.Triggered);
            Assert.Empty(await file.RowsAsync("SELECT name FROM sqlite_master WHERE name='ai_request_batch'"));
            await AssertPreserved(file, before);
        }
        await using (var db = file.Open()) await db.Database.MigrateAsync(Ct);
        await AssertPreserved(file, before);
        await using (var db = file.Open()) await db.Database.MigrateAsync(Ct);
        await AssertPreserved(file, before);
    }

    private static async Task AssertPreserved(AudioTestDatabase file, Dictionary<string, string[]> before)
    {
        var after = await Snapshot(file);
        foreach (var (table, rows) in before) Assert.Equal(rows, after[table]);
        Assert.Empty(await file.RowsAsync("PRAGMA foreign_key_check"));
        Assert.Equal("ok", Assert.IsType<object?[]>(Assert.Single(await file.RowsAsync("PRAGMA integrity_check")))[0]);
        await using var db = file.Open(); await db.Database.OpenConnectionAsync(Ct);
        await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
    }

    private static async Task<Dictionary<string, string[]>> Snapshot(AudioTestDatabase file)
    {
        var result = new Dictionary<string, string[]>();
        foreach (var row in await file.RowsAsync("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EF%' ORDER BY name"))
        {
            var name = (string)((object?[])row!)[0]!;
            result[name] = (await file.RowsAsync("SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\""))
                .Select(r => JsonSerializer.Serialize(r)).Order(StringComparer.Ordinal).ToArray();
        }
        return result;
    }

    private static async Task SeedRelease(AudioTestDatabase file)
    {
        await using var db = file.Open();
        await db.GetService<IMigrator>().MigrateAsync(Release, Ct);
        await AudioTestDatabase.SeedAsync(db);
        db.AudioAssets.Add(AudioTestDatabase.Asset("existing"));
        db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("existing", AudioLocale.EnGb, "meaning", AudioTestDatabase.Revision(AudioLocale.EnGb, true)));
        await db.SaveChangesAsync(Ct);
        await AudioTestDatabase.SeedSucceededOperationAsync(db, "existing");
        // Synthetic non-secret payloads. Every release table is populated so the
        // preservation comparison cannot pass by merely comparing empty tables.
        await db.Database.OpenConnectionAsync(Ct);
        await using var seed = db.Database.GetDbConnection().CreateCommand();
        seed.CommandText = """
            INSERT INTO admin_session VALUES ('synthetic-handle',9999999999999,'synthetic-protected-payload');
            INSERT INTO service_data_protection_keys VALUES ('lexarbor','synthetic-key-id','synthetic-encrypted-xml');
            INSERT INTO service_audit_logs(id,operator_source,action,target_type,target_id,outcome,occurred_at_utc,metadata_json)
                VALUES ('11111111-1111-1111-1111-111111111111','test','fixture','word','word',0,'2026-11-01T00:00:00Z','{}');
            UPDATE vocabulary_meaning SET example='原始例句',phonetic_uk='ˈæpəl',phonetic_us='ˈæpəl' WHERE id='meaning';
            INSERT INTO meaning_phonetic_epoch VALUES ('meaning','en-GB','word',7);
            INSERT INTO audio_invalidation(asset_id,bucket,object_key,word_id,meaning_id,locale,authorization_epoch,reason,state,
                created_at_unix_ms,updated_at_unix_ms,lease_fence,attempts,next_attempt_at_unix_ms)
                VALUES ('existing','test-audio','audio/existing.mp3','word','meaning','en-GB',7,'specific_phonetic_changed','Waiting',1,2,3,2,9000);
            INSERT INTO meaning_submission(id,source,word,part_of_speech,meaning,example,status,result_book_id,result_word_id,
                result_meaning_id,result_word,result_meaning,result_part_of_speech,created_at_unix_ms,updated_at_unix_ms,
                reviewed_at_unix_ms,ai_review_verdict,review_comment,reviewer_kind)
                VALUES ('submission','fixture','Apple','n.','fruit','An apple.',1,'book','word','meaning','Apple','fruit','n.',1,2,2,'accept','原始审核备注','ai');
            INSERT INTO meaning_submission_ai_review VALUES ('review','attempt','submission','accept','n.','fruit','uk','us','保留结果','synthetic-model',2,1);
            INSERT INTO meaning_submission_auto_review VALUES ('submission','attempt','worker',4,10000,20000,2,'timeout',2,'{"verdict":"accept"}');
            INSERT INTO meaning_submission_review_check VALUES ('review',3);
            INSERT INTO enrichment_target VALUES ('task','word',NULL,'en-GB','Audio',2,3,0);
            INSERT INTO enrichment_round(target_id,number,state,input_revision,source_revision,own_asset_id,configuration_version,
                retry_key,failure_budget,request_count,consumed_failures,plan_json,wait_reason,error_code,next_attempt_at_unix_ms,
                lease_owner,lease_fence,lease_expires_at_unix_ms,created_at_unix_ms,updated_at_unix_ms)
                VALUES ('task',1,'Failed',lower(hex(zeroblob(32))),lower(hex(zeroblob(32))),'existing','original-model','original-retry',5,5,5,
                    '{"SpokenText":"Apple","Phonetic":"uk"}',NULL,'tts.timeout',NULL,NULL,5,NULL,1,2),
                    ('task',2,'Running',lower(hex(zeroblob(32))),lower(hex(zeroblob(32))),NULL,'original-model',NULL,5,1,0,
                    '{"SpokenText":"Apple","Phonetic":"uk"}','LeaseExpired',NULL,NULL,'old-worker',9,9999999999999,3,4);
            INSERT INTO enrichment_call VALUES ('failed-call','task',1,'tts','TerminalFailure',NULL,'tts.timeout',1,2),
                ('open-call','task',2,'tts','Started','22222222-2222-2222-2222-222222222222',NULL,3,NULL);
            INSERT INTO enrichment_wait VALUES ('wait','task',1,'Retry','tts.timeout',1,2);
            INSERT INTO enrichment_discovery VALUES ('words','other','word'),('meanings','meaning','other-meaning');
            INSERT INTO enrichment_service VALUES ('text.fixture','original-model',5,32,1,0,300,9999999999999,NULL,NULL);
            INSERT INTO enrichment_service_probe VALUES ('probe','text.fixture','Unknown',1,1,2);
            """;
        await seed.ExecuteNonQueryAsync(Ct);
    }

    private sealed class MigrationFault(string point) : DbCommandInterceptor
    {
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Triggered && command.CommandText.Contains(point, StringComparison.Ordinal)
                && (point != "INSERT INTO \"__EFMigrationsHistory\"" || command.CommandText.Contains(Expansion, StringComparison.Ordinal)))
            { Triggered = true; throw new InvalidOperationException("Injected expansion migration interruption."); }
            return ValueTask.FromResult(result);
        }
    }
}
