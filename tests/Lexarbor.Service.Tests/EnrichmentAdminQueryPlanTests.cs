using System.Data.Common;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("lead")]
    public async Task AdminSummary_DefaultAndSearchUseIndexedTargetLookups(string? search)
    {
        await using var f = await Fixture.Create();
        await f.Ensure(Key());
        var capture = new SummaryQueryCapture();
        await using var db = f.Open(capture);
        await new EnrichmentAdminRepository(db).SummaryAsync(new(Search: search), await AdminConfig(f), f.Clock.Now, Ct);
        // Both the state totals and the activity totals must avoid probing all
        // NULL-meaning targets for each word. A small fixture can hide that cost.
        Assert.Equal(2, capture.Commands.Count);
        foreach (var (sql, parameters) in capture.Commands)
        {
            var plan = await PostgreSqlPlan.ExplainAsync(db.Database.GetConnectionString()!, sql, parameters);
            Assert.Contains("IX_enrichment_target_word_id_locale_step", plan, StringComparison.Ordinal);
            Assert.Contains("IX_enrichment_target_word_id_meaning_id_locale_step", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("Seq Scan on enrichment_target matched", plan, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AdminSummary_IndexedMatchesPreserveDefaultSpecificExpansionAndMissingTargets()
    {
        await using var f = await Fixture.Create();
        await f.Db.VocabularyMeanings.ExecuteUpdateAsync(s => s.SetProperty(m => m.PhoneticUk, "liːd"), Ct);
        var fields = await f.Ensure(Key(EnrichmentStep.Fields));
        var guard = (await f.Tasks.ClaimAsync(fields.Id, "test", 1000, 9000, Ct))!;
        await f.Tasks.StopAsync(guard, "fields.candidates_missing", 1000, Ct);
        var specific = await f.Ensure(Key(meaning: "meaning"));
        guard = (await f.Tasks.ClaimAsync(specific.Id, "test", 1000, 9000, Ct))!;
        await f.Tasks.WaitAsync(guard, EnrichmentWaitReason.Throttled, 1000, 8000, Ct);
        var expansion = await f.Ensure(new("word", "meaning", null, EnrichmentStep.Expand));
        guard = (await f.Tasks.ClaimAsync(expansion.Id, "test", 1000, 9000, Ct))!;
        Assert.True(await f.Tasks.CompleteAsync(guard, 1000, Ct));
        var deleted = await f.Ensure(Key(locale: AudioLocale.EnUs));
        guard = (await f.Tasks.ClaimAsync(deleted.Id, "test", 1000, 9000, Ct))!;
        await f.Tasks.StopAsync(guard, "tts.failed", 1000, Ct);
        await f.Db.EnrichmentTargets.Where(t => t.Id == deleted.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Deleted, true), Ct);
        await f.Ensure(Key(EnrichmentStep.Prepare));
        // Another word's default target must never match a NULL meaning by itself.
        f.Db.Vocabularies.Add(new() { Id = "other", Word = "other" });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        await f.Ensure(new("other", null, AudioLocale.EnGb, EnrichmentStep.Audio));
        var before = await Dump(f.Db);
        var c = (await AdminConfig(f)) with { ExpansionBookId = "book" };
        var admin = new EnrichmentAdminRepository(f.Db);
        var summary = await admin.SummaryAsync(new(WordId: "word"), c, 1000, Ct);
        Assert.Equal(6, summary.ApplicableTargets);
        Assert.Equal(1, summary.CompletedTargets);
        Assert.Equal(3, summary.UnqueuedTargets);
        Assert.Equal(5, summary.PersistedTasks);
        Assert.Contains(new("Failed", null, 1), summary.Targets);
        Assert.Contains(new("Waiting", "Throttled", 1), summary.Targets);
        Assert.Contains(new("Succeeded", null, 1), summary.Targets);
        Assert.Contains(new("Pending", null, 3), summary.Targets);
        var meaning = await admin.SummaryAsync(new(MeaningId: "meaning"), c, 1000, Ct);
        Assert.Equal(2, meaning.ApplicableTargets);
        Assert.Equal(1, meaning.CompletedTargets);
        Assert.Equal(0, meaning.UnqueuedTargets);
        var us = await admin.SummaryAsync(new(WordId: "word", Locale: "en-US"), c, 1000, Ct);
        Assert.Equal(2, us.ApplicableTargets);
        Assert.Equal(2, us.UnqueuedTargets);
        Assert.Equal(new("Pending", null, 2), Assert.Single(us.Targets));
        Assert.Equal(before, await Dump(f.Db));
    }

    [Fact]
    public async Task AutomaticRecovery_FindsMissingWorkAmongCompletedCatalogDemands()
    {
        await using var f = await Fixture.Create();
        var task = await f.Ensure(Key(EnrichmentStep.Prepare));
        await FailLocally(f, task, "text.invalid_plan");
        // Match the catalog size from #436. Older failed Fields rounds have
        // already had their phonetics supplied, so none may consume the limit.
        await f.Db.Database.ExecuteSqlRawAsync("""
            INSERT INTO vocabulary (id, word, phonetic_uk, phonetic_us, created_at, updated_at)
            SELECT 'bulk-' || i, 'bulk-' || i, 'a', 'a', now(), now() FROM generate_series(1, 7000) i;
            INSERT INTO vocabulary_meaning (id, vocabulary_id, book_id, meaning, part_of_speech, created_at, updated_at)
            SELECT id, id, 'book', 'meaning', 'n.', now(), now() FROM vocabulary WHERE id LIKE 'bulk-%';
            INSERT INTO enrichment_target (id, word_id, locale, step, current_round, epoch, deleted)
            SELECT w.id || l.locale || s.step, w.id, l.locale, s.step, 2, 1, false
            FROM vocabulary w CROSS JOIN (VALUES ('en-GB'), ('en-US')) l(locale)
            CROSS JOIN (VALUES ('Fields'), ('Audio')) s(step) WHERE w.id LIKE 'bulk-%';
            INSERT INTO enrichment_round (target_id, number, state, error_code, input_revision, source_revision,
                configuration_version, force_single, fields_not_required, failure_budget, request_count,
                consumed_failures, lease_fence, created_at_unix_ms, updated_at_unix_ms)
            SELECT id, n, CASE WHEN n=1 THEN 'Superseded' WHEN step='Fields' THEN 'Failed' ELSE 'Succeeded' END,
                CASE WHEN n=2 AND step='Fields' THEN 'fields.candidates_missing' END,
                repeat('a',64), repeat('b',64), 'bulk', false, false, 5, 1, 0, 0, 1, 1
            FROM enrichment_target CROSS JOIN generate_series(1, 2) n WHERE word_id LIKE 'bulk-%';
            ANALYZE vocabulary;
            ANALYZE vocabulary_meaning;
            ANALYZE enrichment_target;
            ANALYZE enrichment_round;
            """, Ct);

        Assert.Equal(1, await f.Tasks.RecoverAsync(1, [], await AdminConfig(f), f.Clock.Now, Ct));
        Assert.Equal(task.Id, (await f.Db.EnrichmentRecoveries.SingleAsync(Ct)).TargetId);
    }

    [Fact]
    public async Task AutomaticRecovery_RechecksOneIndexedWordInsideTheWriter()
    {
        await using var f = await Fixture.Create();
        var task = await f.Ensure(Key(EnrichmentStep.Prepare));
        await FailLocally(f, task, "text.invalid_plan");
        var capture = new SummaryQueryCapture { RecoveryOnly = true };
        await using var db = f.Open(capture);
        Assert.Equal(1, await new EnrichmentTaskRepository(db, f.Clock).RecoverAsync(5, [], await AdminConfig(f), f.Clock.Now, Ct));
        var (sql, parameters) = Assert.Single(capture.Commands);
        var plan = await PostgreSqlPlan.ExplainAsync(db.Database.GetConnectionString()!, sql, parameters);
        Assert.Contains("on vocabulary w", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan on vocabulary w", plan, StringComparison.Ordinal);
    }

    private sealed class SummaryQueryCapture : DbCommandInterceptor
    {
        public bool RecoveryOnly;
        public List<(string Sql, DbParameter[] Parameters)> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("WITH locales", StringComparison.Ordinal)
                && (RecoveryOnly ? eventData.Context?.Database.CurrentTransaction != null : command.CommandText.Contains("GROUP BY", StringComparison.Ordinal)))
                Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
