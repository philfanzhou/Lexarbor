using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class AudioSelectionTests
{
    [Fact]
    public void Revisions_AreStableVersionedAndDistinguishNullEmptyAndFieldBoundaries()
    {
        var word = new AudioWordInput("word", "Nobel Prize", null, "us");
        var input = AudioSelectionService.GetInput(word, null, AudioLocale.EnGb);
        var revision = AudioSelectionService.ComputeRevision(input);
        Assert.Equal("40dc8147ccb69f95aa867491483c5582941f05146d9730ff0a9483e48be0a41a", revision);
        Assert.Equal(revision, AudioSelectionService.ComputeRevision(input));
        Assert.Matches("^[0-9a-f]{64}$", revision);
        Assert.NotEqual(revision, AudioSelectionService.ComputeRevision(input with { Phonetic = "" }));
        Assert.NotEqual(revision, AudioSelectionService.ComputeRevision(input with { Word = "nobel prize" }));
        Assert.NotEqual(revision, AudioSelectionService.ComputeRevision(input with { Locale = AudioLocale.EnUs }));
        var meaning = AudioSelectionService.GetInput(word, new("meaning", null, "a|b"), AudioLocale.EnGb);
        Assert.NotEqual(AudioSelectionService.ComputeRevision(meaning), AudioSelectionService.ComputeRevision(meaning with { PartOfSpeech = "" }));
        Assert.NotEqual(AudioSelectionService.ComputeRevision(meaning with { PartOfSpeech = "a", Meaning = "b|c" }),
            AudioSelectionService.ComputeRevision(meaning with { PartOfSpeech = "a|b", Meaning = "c" }));
        Assert.Equal(revision, AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(word with { WordId = "another-id" }, null, AudioLocale.EnGb)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioSelectionService.GetInput(word, null, (AudioLocale)99));
    }

    [Fact]
    public void Selection_UsesStaleOverrideBeforeDefaultAndNeverCrossesAccents()
    {
        var word = new AudioWordInput("word", "Apple", "uk", "us");
        var meaning = new AudioMeaningInput("meaning", "n.", "fruit");
        var asset = new AudioAsset("default", new("test-audio", "audio/default.mp3"), AudioTestDatabase.Hash, 128, "audio/mpeg", 1, 2);
        var baseline = new AudioBinding("word", null, AudioLocale.EnGb, asset, AudioTestDatabase.Revision(AudioLocale.EnGb));
        var snapshot = new AudioReadSnapshot(word, meaning, [baseline]);
        var selection = AudioSelectionService.Select(snapshot, AudioLocale.EnGb);
        Assert.Null(selection.OwnBinding); Assert.Equal(baseline, selection.EffectiveBinding);
        Assert.Equal(AudioBindingSource.Default, selection.Source); Assert.Equal(AudioContentStatus.Ready, selection.Status);
        var stale = new AudioBinding("word", "meaning", AudioLocale.EnGb, asset with { Id = "override" }, AudioTestDatabase.Hash);
        selection = AudioSelectionService.Select(snapshot with { Bindings = [baseline, stale] }, AudioLocale.EnGb);
        Assert.Equal(stale, selection.OwnBinding); Assert.Equal(stale, selection.EffectiveBinding);
        Assert.Equal(AudioBindingSource.Meaning, selection.Source); Assert.Equal(AudioContentStatus.Stale, selection.Status);
        selection = AudioSelectionService.Select(snapshot with { Bindings = [baseline, stale] }, AudioLocale.EnUs);
        Assert.Equal(AudioContentStatus.Missing, selection.Status); Assert.Null(selection.EffectiveBinding); Assert.Null(selection.Source);
        selection = AudioSelectionService.Select(snapshot with { Bindings = [] }, AudioLocale.EnGb);
        Assert.Equal(AudioContentStatus.Missing, selection.Status);
        selection = AudioSelectionService.Select(snapshot with { Meaning = null }, AudioLocale.EnGb);
        Assert.Equal(baseline, selection.OwnBinding); Assert.Equal(AudioContentStatus.Ready, selection.Status);
    }

    [Fact]
    public void DomainAudioPorts_HaveNoInfrastructureReferences()
    {
        var names = typeof(AudioSelectionService).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToList();
        Assert.DoesNotContain(names, name => name.Contains("EntityFramework", StringComparison.Ordinal) || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || name.Contains("Minio", StringComparison.Ordinal));
    }
}
