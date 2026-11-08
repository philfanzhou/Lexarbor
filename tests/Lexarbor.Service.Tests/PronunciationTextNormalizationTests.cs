using System.Text.Json.Nodes;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Integration;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Service.Tests;

public sealed partial class PronunciationSpeechTests
{
    [Theory]
    [InlineData("well-known", "well known", "wel nəʊn")]
    [InlineData("well–known", "well known", "wel nəʊn")]
    [InlineData("U.S.", "U S", "juː es")]
    [InlineData("don't", "don't", "dəʊnt")]
    [InlineData("can’t", "can't", "kɑːnt")]
    [InlineData("re-sign", "re sign", "riː saɪn")]
    [InlineData("St.", "Saint", "seɪnt")]
    [InlineData("St.", "Street", "striːt")]
    [InlineData("Dr.", "Doctor", "dɒktə")]
    public async Task NativePunctuation_KeepsCatalogSpellingAndBindsIpaToExactlyTheSpokenText(string word, string spoken, string ipa)
    {
        foreach (var locale in new[] { AudioLocale.EnGb, AudioLocale.EnUs })
        {
            using var f = new Fixture(true);
            f.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? IpaCompletion(spoken, ipa, ipa) : Native());
            var request = NotationRequest(word, ipa, locale);
            var result = await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime).PrepareAsync(request, Ct);
            Assert.Equal(PronunciationPreparationOutcome.Ready, result.Outcome);
            Assert.Equal(word, result.Plan!.Input.Word);
            Assert.Equal(PronunciationSpeechOutcome.Ready, (await f.Client.GenerateAsync(result, Ct)).Outcome);
            var body = JsonNode.Parse(f.Handler.Bodies[1])!;
            Assert.Equal(spoken, body["text"]!.GetValue<string>());
            Assert.Equal(spoken + "/(" + ipa + ")", body["pronunciation_dict"]!["tone"]![0]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("re-sign", "resign")]
    [InlineData("U.S.", "us")]
    [InlineData("can't", "cant")]
    [InlineData("well-known", "wellknown")]
    public async Task ProposedDefaultPhonetic_RejectsChangedWordsBeforeFieldPublication(string word, string spoken)
    {
        using var f = new Fixture(true);
        await f.Publish(new() { ["Enrichment:FieldsEnabled"] = "true" });
        f.Handler.Respond = (_, _) => Task.FromResult(IpaCompletion(spoken, null, "test", "test", kind: "word"));
        var result = await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime).PrepareAsync(NotationRequest(word, null, AudioLocale.EnGb), Ct);
        Assert.Equal(PronunciationPreparationOutcome.InvalidPlan, result.Outcome);
        Assert.Equal("pronunciation_text_mismatch", result.ErrorCode);
        Assert.Null(result.Plan); Assert.Single(f.Handler.Bodies);
        await f.Publish(new() { ["Enrichment:Tts:ApiKey"] = "" });
        Assert.Equal(PronunciationPreparationOutcome.Ready,
            (await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime).PrepareAsync(NotationRequest(word, null, AudioLocale.EnGb), Ct)).Outcome);
    }

    [Theory]
    [InlineData("U.S.", "U S")]
    [InlineData("well-known", "well known")]
    [InlineData("re-sign", "re sign")]
    [InlineData("don’t", "don't")]
    public void TypographyNormalization_IsDeterministicAndIdempotent(string source, string expected)
    {
        Assert.Equal(expected, PronunciationText.Normalize(source));
        Assert.Equal(expected, PronunciationText.Normalize(expected));
    }
}

public sealed partial class EnrichmentTargetExecutorTests
{
    [Theory]
    [InlineData("well-known", "well known", "wel nəʊn")]
    [InlineData("U.S.", "U S", "juː es")]
    [InlineData("re-sign", "re sign", "riː saɪn")]
    [InlineData("don't", "don't", "dəʊnt")]
    public async Task NewDefaultIpa_DoesNotInvalidateNormalizedSpeechAfterFieldsCommit(string source, string spoken, string ipa)
    {
        await using var f = await Fixture.Create(native: true);
        await f.Db.Vocabularies.ExecuteUpdateAsync(s => s.SetProperty(w => w.Word, source), Ct);
        f.Http.Ipa = true; f.Http.SpokenText = source; f.Http.Phonetic = ipa;
        var fields = (await f.Run(Key(EnrichmentStep.Fields)))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(fields).State);
        var audio = (await f.Run(Key()))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(audio).State);
        Assert.Equal(1, f.Http.TextCalls); Assert.Equal(1, f.Http.SpeechCalls);
        Assert.Equal(spoken, f.Http.SpeechText); Assert.Equal(spoken + "/(" + ipa + ")", f.Http.SpeechTone);
        Assert.Equal(source, await f.Db.Vocabularies.Select(w => w.Word).SingleAsync(Ct));
        Assert.Equal(ipa, await f.Db.Vocabularies.Select(w => w.PhoneticUk).SingleAsync(Ct));
        Assert.Single(await f.Db.VocabularyAudioBindings.ToArrayAsync(Ct));
    }
}
