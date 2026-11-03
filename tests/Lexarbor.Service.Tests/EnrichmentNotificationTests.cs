using Lexarbor.Domain.Models;
using Lexarbor.Host;

namespace Lexarbor.Service.Tests;

public sealed class EnrichmentNotificationTests
{
    [Fact]
    public async Task ExactAudioTargets_AreIndependentOfMeaningPageAndDeduplicatedBeforeDispatch()
    {
        var notifications = new EnrichmentNotifications();
        var targets = Enumerable.Range(0, 25).Select(i => new EnrichmentTargetKey("word", $"meaning-{i:D2}", AudioLocale.EnGb, EnrichmentStep.Audio)).ToArray();
        notifications.Notify(["word"], [.. targets, targets[^1]]);
        await notifications.WaitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal("word", Assert.Single(notifications.Drain()));
        var drained = notifications.DrainAudioTargets();
        Assert.Equal(25, drained.Count); Assert.Contains(targets[^1], drained);
        Assert.Empty(notifications.DrainAudioTargets());
    }

    [Fact]
    public void ExactAudioNotifications_RemainBoundedAndKeepTheirWordFallback()
    {
        var notifications = new EnrichmentNotifications();
        notifications.Notify(["word"], Enumerable.Range(0, 300).Select(i => new EnrichmentTargetKey("word", i.ToString(), AudioLocale.EnUs, EnrichmentStep.Audio)).ToArray());
        Assert.Equal(256, notifications.DrainAudioTargets().Count);
        Assert.Equal("word", Assert.Single(notifications.Drain()));
    }
}
