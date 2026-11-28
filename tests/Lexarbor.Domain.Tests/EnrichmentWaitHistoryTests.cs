using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// A worker polls a busy permit or an unfinished dependency about once a second. Those polls must not
/// add a history row each; waits that follow a supplier call still record every call.
/// </summary>
public sealed class EnrichmentWaitHistoryTests
{
    private const string PreviousMigration = "20261119072530_AddLearningScope";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RepeatedPollingWaitsExtendTheLatestRow()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await db.Database.MigrateAsync(Ct); await AudioTestDatabase.SeedAsync(db);
        var tasks = new EnrichmentTaskRepository(db);
        // The repository never records a time before its own clock, so the test runs ahead of it.
        var now = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        var task = (await tasks.EnsureAsync(new("word", "meaning", null, EnrichmentStep.Expand), "fixture", now, Ct))!;

        async Task WaitAsync(EnrichmentWaitReason reason)
        {
            var guard = (await tasks.ClaimAsync(task.Id, "fixture", now, now + 10_000, Ct))!;
            Assert.True(await tasks.WaitAsync(guard, reason, now, now + 1_000, Ct));
            now += 1_000;
        }

        var first = now;
        for (var i = 0; i < 5; i++) await WaitAsync(EnrichmentWaitReason.Throttled);
        var throttledUntil = now;
        await WaitAsync(EnrichmentWaitReason.PreparationPending);
        await WaitAsync(EnrichmentWaitReason.PreparationPending);
        await WaitAsync(EnrichmentWaitReason.Throttled);

        var waits = await db.EnrichmentWaits.AsNoTracking().OrderBy(w => w.RecordedAtUnixMs).ToArrayAsync(Ct);
        Assert.Equal([EnrichmentWaitReason.Throttled, EnrichmentWaitReason.PreparationPending, EnrichmentWaitReason.Throttled],
            waits.Select(w => w.Reason));
        Assert.Equal(first, waits[0].RecordedAtUnixMs);
        Assert.Equal(throttledUntil, waits[0].NextAttemptAtUnixMs);
        Assert.Equal(now, waits[2].NextAttemptAtUnixMs);
    }

    [Fact]
    public async Task SupplierThrottlesKeepOneRowPerCall()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open(); await db.Database.MigrateAsync(Ct); await AudioTestDatabase.SeedAsync(db);
        var tasks = new EnrichmentTaskRepository(db);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var task = (await tasks.EnsureAsync(new("word", "meaning", null, EnrichmentStep.Expand), "fixture", now, Ct))!;
        for (var i = 0; i < 3; i++)
        {
            var guard = (await tasks.ClaimAsync(task.Id, "fixture", now, now + 10_000, Ct))!;
            var call = Guid.NewGuid().ToString();
            Assert.True(await tasks.BeginCallAsync(guard, call, "text", null, now, Ct));
            Assert.True(await tasks.FinishCallAsync(guard, call, EnrichmentCallResult.Throttled, "expansion.throttled", now, now + 5_000, Ct));
            now += 5_000;
        }

        Assert.Equal(3, await db.EnrichmentWaits.CountAsync(Ct));
    }

    [Fact]
    public async Task UpgradeCollapsesRecordedPollingRuns()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken: Ct);
        await AudioTestDatabase.SeedAsync(db);
        var tasks = new EnrichmentTaskRepository(db);
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var task = (await tasks.EnsureAsync(new("word", "meaning", null, EnrichmentStep.Expand), "fixture", start, Ct))!;
        var other = (await tasks.EnsureAsync(new("other", null, AudioLocale.EnGb, EnrichmentStep.Fields), "fixture", start, Ct))!;
        var history = new (EnrichmentWaitReason Reason, string? Code)[]
        {
            (EnrichmentWaitReason.Throttled, null), (EnrichmentWaitReason.Throttled, null), (EnrichmentWaitReason.Throttled, null),
            (EnrichmentWaitReason.Throttled, "tts.rate_limited"), (EnrichmentWaitReason.Throttled, "tts.rate_limited"),
            (EnrichmentWaitReason.Retry, "text.invalid_plan"), (EnrichmentWaitReason.Retry, "text.invalid_plan"),
            (EnrichmentWaitReason.PreparationPending, null), (EnrichmentWaitReason.PreparationPending, null),
            (EnrichmentWaitReason.LeaseExpired, null), (EnrichmentWaitReason.LeaseExpired, null),
            (EnrichmentWaitReason.Throttled, null),
        };
        for (var i = 0; i < history.Length; i++) db.EnrichmentWaits.Add(Wait(task, history[i].Reason, history[i].Code, start + i * 1_000));
        db.EnrichmentWaits.Add(Wait(other, EnrichmentWaitReason.Throttled, null, start));
        await db.SaveChangesAsync(Ct);

        await db.Database.MigrateAsync(Ct);

        var kept = await db.EnrichmentWaits.AsNoTracking().Where(w => w.TargetId == task.Id)
            .OrderBy(w => w.RecordedAtUnixMs).ToArrayAsync(Ct);
        Assert.Equal([0, 3, 4, 5, 6, 7, 9, 10, 11], kept.Select(w => (int)((w.RecordedAtUnixMs - start) / 1_000)));
        Assert.Equal(start + 2_000 + 1_000, kept[0].NextAttemptAtUnixMs);
        Assert.Equal(start + 8_000 + 1_000, kept[5].NextAttemptAtUnixMs);
        Assert.Equal(start + 3_000 + 1_000, kept[1].NextAttemptAtUnixMs);
        Assert.Equal(1, await db.EnrichmentWaits.CountAsync(w => w.TargetId == other.Id, Ct));
    }

    private static EnrichmentWaitEntity Wait(EnrichmentTask task, EnrichmentWaitReason reason, string? code, long at) => new()
    {
        Id = Guid.NewGuid().ToString(),
        TargetId = task.Id,
        Round = task.CurrentRound,
        Reason = reason,
        ErrorCode = code,
        RecordedAtUnixMs = at,
        NextAttemptAtUnixMs = at + 1_000,
    };
}
