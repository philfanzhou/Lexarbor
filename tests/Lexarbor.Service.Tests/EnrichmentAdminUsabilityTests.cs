using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    [Fact]
    public async Task AdminAttention_SeparatesAutomaticWaitingAndOrdersFailuresFirstWithoutWrites()
    {
        await using var f = await Fixture.Create();
        var uk = await f.Ensure(Key());
        var us = await f.Ensure(Key(locale: AudioLocale.EnUs));
        var guard = (await f.Tasks.ClaimAsync(uk.Id, "test", 1000, 9000, Ct))!;
        await f.Tasks.WaitAsync(guard, EnrichmentWaitReason.Throttled, 1001, 8000, Ct);
        guard = (await f.Tasks.ClaimAsync(us.Id, "test", 1000, 9000, Ct))!;
        await f.Tasks.StopAsync(guard, "tts.failed", 1002, Ct);
        var c = (await AdminConfig(f)) with { ExpansionBookId = "book" };
        var admin = new EnrichmentAdminRepository(f.Db);
        var before = await Dump(f.Db);
        var attention = await admin.ListAsync(new(AttentionOnly: true), c, 1003, Ct);
        Assert.Equal(us.Id, Assert.Single(attention.Items).Id);
        var first = await admin.ListAsync(new(Size: 1, Sort: "attention"), c, 1003, Ct);
        var second = await admin.ListAsync(new(Page: 2, Size: 1, Sort: "attention"), c, 1003, Ct);
        Assert.Equal(us.Id, Assert.Single(first.Items).Id);
        Assert.Equal(uk.Id, Assert.Single(second.Items).Id);
        Assert.Equal(us.Id, Assert.Single((await admin.ListAsync(new(Size: 1, Sort: "recent"), c, 1003, Ct)).Items).Id);
        var summary = await admin.SummaryAsync(new(), c, 1003, Ct);
        Assert.Equal(summary.ApplicableTargets, summary.Activities!.Sum(a => a.Total));
        Assert.Equal(summary.CompletedTargets, summary.Activities!.Sum(a => a.Completed));
        Assert.Equal(summary.UnqueuedTargets, summary.Activities!.Sum(a => a.Unqueued));
        Assert.Equal(1, summary.Activities!.Single(a => a.Step == "Audio").ActionRequired);
        Assert.All(summary.Activities!, a => Assert.Empty(a.MissingRequirements));
        Assert.Equal(before, await Dump(f.Db));
    }

    [Fact]
    public async Task AdminAttention_ExcludesPreparationPending_ButKeepsCurrentConfigurationGap()
    {
        await using var f = await Fixture.Create();
        var uk = await f.Ensure(Key());
        var us = await f.Ensure(Key(locale: AudioLocale.EnUs));
        var guard = (await f.Tasks.ClaimAsync(uk.Id, "test", 1000, 9000, Ct))!;
        await f.Tasks.WaitAsync(guard, EnrichmentWaitReason.PreparationPending, 1001, 8000, Ct);
        guard = (await f.Tasks.ClaimAsync(us.Id, "test", 1000, 9000, Ct))!;
        await f.Tasks.WaitAsync(guard, EnrichmentWaitReason.ConfigurationMissing, 1002, 8000, Ct);
        await f.Publish(new() { ["Enrichment:Tts:Us:Voice"] = "" });
        var c = (await AdminConfig(f)) with { ExpansionBookId = "book" };
        var admin = new EnrichmentAdminRepository(f.Db);
        var attention = await admin.ListAsync(new(AttentionOnly: true), c, 1003, Ct);
        Assert.Equal(us.Id, Assert.Single(attention.Items).Id);
        var summary = await admin.SummaryAsync(new(), c, 1003, Ct);
        Assert.Equal(1, summary.Activities!.Single(a => a.Step == "Audio").ActionRequired);
        Assert.Equal(0, summary.Activities!.Single(a => a.Step == "Fields").ActionRequired);
    }

    [Fact]
    public async Task AdminAttention_ProjectsLegacyDependencyWaitWithoutRewritingTheRow()
    {
        await using var f = await Fixture.Create();
        var uk = await f.Ensure(Key());
        var guard = (await f.Tasks.ClaimAsync(uk.Id, "test", 1000, 9000, Ct))!;
        await f.Tasks.WaitAsync(guard, EnrichmentWaitReason.ConfigurationMissing, 1001, 8000, Ct);
        var c = await AdminConfig(f);
        var admin = new EnrichmentAdminRepository(f.Db);
        var before = await Dump(f.Db);
        var detail = (await admin.DetailAsync(uk.Id, 1, 10, c, 1003, Ct))!;
        Assert.Equal("PreparationPending", detail.Task.WaitReason);
        Assert.Empty((await admin.ListAsync(new(AttentionOnly: true), c, 1003, Ct)).Items);
        Assert.Equal(before, await Dump(f.Db));
    }

    [Fact]
    public async Task AdminActivities_ExplainMissingConfigurationEvenBeforeDiscoveryAndRespectAccent()
    {
        await using var f = await Fixture.Create();
        var c = (await AdminConfig(f)) with { CanPrepare = false, StorageEnabled = false, ExpansionBookId = "missing" };
        var admin = new EnrichmentAdminRepository(f.Db);
        var before = await Dump(f.Db);
        var summary = await admin.SummaryAsync(new(), c, 1000, Ct);
        Assert.Equal(summary.ApplicableTargets, summary.UnqueuedTargets);
        Assert.Contains("text", summary.Activities!.Single(a => a.Step == "Fields").MissingRequirements);
        Assert.Equal(new[] { "text", "supplement" }, summary.Activities!.Single(a => a.Step == "Expand").MissingRequirements);
        Assert.Contains("storage", summary.Activities!.Single(a => a.Step == "Audio").MissingRequirements);
        c = c with { CanPrepare = true, CanTtsUk = true, CanTtsUs = false, StorageEnabled = true, ExpansionBookId = "book" };
        summary = await admin.SummaryAsync(new(), c, 1000, Ct);
        Assert.Equal(new[] { "usSpeech" }, summary.Activities!.Single(a => a.Step == "Audio").MissingRequirements);
        summary = await admin.SummaryAsync(new(Locale: "en-GB"), c, 1000, Ct);
        Assert.Empty(summary.Activities!.Single(a => a.Step == "Audio").MissingRequirements);
        Assert.Equal(0, summary.Activities!.Single(a => a.Step == "Expand").Total);
        Assert.Equal(before, await Dump(f.Db));
        await f.Db.VocabularyBooks.ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, false), Ct);
        summary = await admin.SummaryAsync(new(), c, 1000, Ct);
        Assert.Contains("supplement", summary.Activities!.Single(a => a.Step == "Expand").MissingRequirements);
    }

    [Fact]
    public async Task AdminSearch_MatchesWordsAndSpecificMeaningsAndTreatsWildcardsLiterally()
    {
        await using var f = await Fixture.Create();
        await f.Ensure(Key());
        await f.Db.VocabularyMeanings.ExecuteUpdateAsync(s => s.SetProperty(m => m.PhoneticUk, "liːd")
            .SetProperty(m => m.Meaning, "引导 100%_literal"), Ct);
        var specific = await f.Ensure(Key(meaning: "meaning"));
        var admin = new EnrichmentAdminRepository(f.Db); var c = await AdminConfig(f);
        var before = await Dump(f.Db);
        Assert.Equal(2, (await admin.ListAsync(new(Search: "  LEAD  "), c, 1000, Ct)).TotalCount);
        Assert.Equal(specific.Id, Assert.Single((await admin.ListAsync(new(Search: "引导"), c, 1000, Ct)).Items).Id);
        Assert.Equal(specific.Id, Assert.Single((await admin.ListAsync(new(Search: "%_"), c, 1000, Ct)).Items).Id);
        Assert.Empty((await admin.ListAsync(new(Search: "' OR 1=1 --"), c, 1000, Ct)).Items);
        Assert.Equal(2, (await admin.SummaryAsync(new(Search: "引导"), c, 1000, Ct)).ApplicableTargets); // specific UK and expansion
        Assert.Equal(before, await Dump(f.Db));
    }
}
