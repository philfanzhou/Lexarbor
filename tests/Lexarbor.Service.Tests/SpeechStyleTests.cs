using System.Text.Json.Nodes;
using Lexarbor.Domain.Models;

namespace Lexarbor.Service.Tests;

public sealed partial class PronunciationSpeechTests
{
    [Theory]
    [InlineData(true, AudioLocale.EnGb)]
    [InlineData(true, AudioLocale.EnUs)]
    [InlineData(false, AudioLocale.EnGb)]
    [InlineData(false, AudioLocale.EnUs)]
    public async Task SavedStyles_AreAppliedToTheSelectedLocale_AndKeepPronunciationConstraints(bool native, AudioLocale locale)
    {
        using var f = new Fixture(native);
        await f.Publish(new()
        {
            ["Enrichment:Tts:Uk:Style"] = "Calm teaching voice.",
            ["Enrichment:Tts:Us:Style"] = "Bright and friendly.",
            ["Enrichment:Tts:Uk:Emotion"] = "calm",
            ["Enrichment:Tts:Us:Emotion"] = "happy",
            ["Enrichment:Tts:Uk:Pitch"] = "-2",
            ["Enrichment:Tts:Us:Pitch"] = "3",
            ["Enrichment:Tts:Uk:Volume"] = "0.8",
            ["Enrichment:Tts:Us:Volume"] = "1.2"
        });
        Assert.Equal(PronunciationSpeechOutcome.Ready, (await f.Client.GenerateAsync(f.Plan(locale: locale), Ct)).Outcome);
        var body = JsonNode.Parse(Assert.Single(f.Handler.Bodies))!;
        var uk = locale == AudioLocale.EnGb;
        if (native)
        {
            Assert.Equal(uk ? "calm" : "happy", body["voice_setting"]!["emotion"]!.GetValue<string>());
            Assert.Equal(uk ? -2 : 3, body["voice_setting"]!["pitch"]!.GetValue<int>());
            Assert.Equal(uk ? 0.8m : 1.2m, body["voice_setting"]!["vol"]!.GetValue<decimal>());
            Assert.Equal("lead/(liːd)", body["pronunciation_dict"]!["tone"]![0]!.GetValue<string>());
        }
        else
        {
            var instructions = body["instructions"]!.GetValue<string>();
            Assert.Contains(uk ? "Calm teaching voice." : "Bright and friendly.", instructions);
            Assert.DoesNotContain(uk ? "Bright and friendly." : "Calm teaching voice.", instructions);
            Assert.Contains("liːd", instructions);
            Assert.Contains("take precedence over delivery style", instructions);
            Assert.Equal("lead", body["input"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("tts-1")]
    [InlineData("tts-1-hd")]
    public async Task LegacyModels_CannotSilentlyIgnoreConfiguredStyle(string model)
    {
        using var f = new Fixture();
        await f.Publish(new() { ["Enrichment:Tts:Model"] = model, ["Enrichment:Tts:Uk:Style"] = "Calm." });
        var result = await f.Client.PreviewAsync(f.Runtime.Current.Enrichment, AudioLocale.EnGb, "word", Ct);
        Assert.Equal("capability_unsupported", result.ErrorCode);
        Assert.False(result.RequestMade); Assert.Empty(f.Handler.Bodies);
    }
}
