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
    public void Selection_UsesExplicitStaleOverrideBeforeDefaultAndNeverCrossesAccents()
    {
        var word = new AudioWordInput("word", "Apple", "uk", "us");
        var meaning = new AudioMeaningInput("meaning", "n.", "fruit", "uk");
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

    [Theory]
    [InlineData(AudioLocale.EnGb, null)]
    [InlineData(AudioLocale.EnGb, "")]
    [InlineData(AudioLocale.EnGb, " \t ")]
    [InlineData(AudioLocale.EnUs, null)]
    [InlineData(AudioLocale.EnUs, "")]
    [InlineData(AudioLocale.EnUs, " \t ")]
    public void EmptySpecific_InheritsDefaultWhileKeepingLegacyOwnBinding(AudioLocale locale, string? specific)
    {
        var word = new AudioWordInput("word", "Apple", "uk", "us");
        var meaning = new AudioMeaningInput("meaning", "n.", "fruit", specific, specific);
        var baseline = Binding(null, locale, word, null);
        var legacy = Binding("meaning", locale, word, meaning);
        var selected = AudioSelectionService.Select(new(word, meaning, [baseline, legacy]), locale);
        Assert.Equal(legacy, selected.OwnBinding);
        Assert.Equal(baseline, selected.EffectiveBinding);
        Assert.Equal(AudioBindingSource.Default, selected.Source);
        Assert.Equal(AudioContentStatus.Ready, selected.Status);
        Assert.Equal(AudioSelectionService.ComputeRevision(new("Apple", locale, locale == AudioLocale.EnGb ? "uk" : "us", true, "n.", "fruit")), selected.SourceRevision);
        var missing = AudioSelectionService.Select(new(word, meaning, [legacy]), locale);
        Assert.Equal(legacy, missing.OwnBinding);
        Assert.Null(missing.EffectiveBinding);
        Assert.Null(missing.Source);
        Assert.Equal(AudioContentStatus.Missing, missing.Status);
        var defaultRequest = AudioSelectionService.Select(new(word, null, [baseline, legacy]), locale);
        Assert.Equal(baseline, defaultRequest.OwnBinding);
        Assert.Equal(baseline, defaultRequest.EffectiveBinding);
    }

    [Theory]
    [InlineData(" / uk / ", "[uk]", true)]
    [InlineData("e\u0301", "é", true)]
    [InlineData("[uk]", "/uk/", true)]
    [InlineData("/uk]", "uk", false)]
    [InlineData("[[uk]]", "[uk]", false)]
    [InlineData("UK", "uk", false)]
    [InlineData("u k", "uk", false)]
    [InlineData("ˈuk", "uk", false)]
    [InlineData("uːk", "uk", false)]
    [InlineData("ʊk", "uk", false)]
    [InlineData("ᵃ", "a", false)]
    [InlineData("uk", null, false)]
    [InlineData("uk", "", false)]
    [InlineData("uk", " \t ", false)]
    [InlineData("/ /", "[]", false)]
    public void ExplicitSpecific_FallsBackOnlyForNonemptyEquivalentPhonetics(string specific, string? baselinePhonetic, bool matches)
    {
        foreach (var locale in Enum.GetValues<AudioLocale>())
        {
            var word = new AudioWordInput("word", "Apple", baselinePhonetic, baselinePhonetic);
            var meaning = new AudioMeaningInput("meaning", "n.", "fruit", specific, specific);
            var baseline = Binding(null, locale, word, null);
            var selected = AudioSelectionService.Select(new(word, meaning, [baseline]), locale);
            Assert.Equal(specific, selected.Input.Phonetic);
            Assert.Null(selected.OwnBinding);
            Assert.Equal(matches ? baseline : null, selected.EffectiveBinding);
            Assert.Equal(matches ? AudioBindingSource.Default : (AudioBindingSource?)null, selected.Source);
            Assert.Equal(matches ? AudioContentStatus.Ready : AudioContentStatus.Missing, selected.Status);
            Assert.Equal(AudioContentStatus.Missing, AudioSelectionService.Select(new(word, meaning, []), locale).Status);
            var own = Binding("meaning", locale, word, meaning);
            selected = AudioSelectionService.Select(new(word, meaning, [baseline, own]), locale);
            Assert.Equal(own, selected.OwnBinding);
            Assert.Equal(own, selected.EffectiveBinding);
            Assert.Equal(AudioBindingSource.Meaning, selected.Source);
            Assert.Equal(AudioContentStatus.Ready, selected.Status);
        }
    }

    [Fact]
    public void SpecificRevision_UsesRawValueAndChangesOnlyItsMeaningAndLocale()
    {
        var word = new AudioWordInput("word", "Apple", "uk", "us");
        var original = new AudioMeaningInput("meaning", "n.", "fruit", "uk", "us");
        var changed = original with { PhoneticUk = " /uk/ " };
        static string Revision(AudioWordInput word, AudioMeaningInput? meaning, AudioLocale locale)
            => AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(word, meaning, locale));
        Assert.NotEqual(Revision(word, original, AudioLocale.EnGb), Revision(word, changed, AudioLocale.EnGb));
        Assert.Equal(Revision(word, original, AudioLocale.EnUs), Revision(word, changed, AudioLocale.EnUs));
        Assert.Equal(Revision(word, null, AudioLocale.EnGb), AudioTestDatabase.Revision(AudioLocale.EnGb));
        Assert.Equal(Revision(word, original, AudioLocale.EnGb), Revision(word with { PhoneticUk = "changed-default" }, original, AudioLocale.EnGb));
        Assert.Equal("us", AudioSelectionService.GetInput(word, changed, AudioLocale.EnUs).Phonetic);
        Assert.Equal(" /uk/ ", AudioSelectionService.GetInput(word, changed, AudioLocale.EnGb).Phonetic);
    }

    private static AudioBinding Binding(string? meaningId, AudioLocale locale, AudioWordInput word, AudioMeaningInput? meaning)
    {
        var id = meaningId ?? "default";
        var asset = new AudioAsset(id, new("test-audio", $"audio/{id}.mp3"), AudioTestDatabase.Hash, 128, "audio/mpeg", 1, 2);
        return new(word.WordId, meaningId, locale, asset, AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(word, meaning, locale)));
    }

    [Fact]
    public void DomainAudioPorts_HaveNoInfrastructureReferences()
    {
        var names = typeof(AudioSelectionService).Assembly.GetReferencedAssemblies().Select(name => name.Name!).ToList();
        Assert.DoesNotContain(names, name => name.Contains("EntityFramework", StringComparison.Ordinal) || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || name.Contains("Minio", StringComparison.Ordinal));
    }
}
