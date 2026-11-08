using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Integration;

namespace Lexarbor.Service.Tests;

public sealed partial class PronunciationSpeechTests
{
    private static HttpResponseMessage IpaCompletion(string spoken, string? echo, string ipa, string? candidate = null,
        string control = "ipa", string kind = "phrase") => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                choices = new[] { new { finish_reason = "stop", message = new
        { content = JsonSerializer.Serialize(new { certain = true, entryKind = kind, classificationBasis = "Fixture classification",
            spokenText = spoken, pronunciationHint = ipa, requiredPhonetic = echo, phonetic = candidate,
            partsOfSpeech = Array.Empty<object>(), pronunciationControl = control, speechPhonetic = control == "ipa" ? ipa : null }) } } }
            })
        };

    private static PronunciationPreparationRequest NotationRequest(string word, string? notation, AudioLocale locale)
    {
        var input = new AudioWordInput("word", word, notation, notation);
        return new(input, null, locale, new("fixture", AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(input, null, locale)), null), []);
    }

    [Theory]
    [InlineData("stable", "ˈsteɪb(ə)l", "ˈsteɪbəl")]
    [InlineData("stable", "ˈsteɪb(ə)l", "ˈsteɪbl")]
    [InlineData("stable", " /ˈsteɪb(ə)l/ ", "ˈsteɪbəl")]
    [InlineData("stable", "[ˈsteɪb(ə)l]", "ˈsteɪbl")]
    [InlineData("lead", "/liːd/; [led]", "liːd")]
    [InlineData("lead", "/liːd/; [led]", "led")]
    [InlineData("accident", "ˈæksədənt", "ˈæksədənt")]
    public async Task DictionaryNotation_PreparesPersistsRestoresAndSpeaksOneAllowedReading(string word, string notation, string ipa)
    {
        foreach (var locale in new[] { AudioLocale.EnGb, AudioLocale.EnUs })
        {
            using var f = new Fixture(true);
            f.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? IpaCompletion(word, ipa, ipa) : Native());
            var prepared = await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime).PrepareAsync(NotationRequest(word, notation, locale), Ct);
            Assert.Equal(PronunciationPreparationOutcome.Ready, prepared.Outcome);
            Assert.Equal(notation.Trim(), prepared.Plan!.RequiredPhonetic);
            Assert.Null(prepared.Plan.Content.Phonetic);
            var persisted = JsonSerializer.Deserialize<EnrichmentPlan>(JsonSerializer.Serialize(EnrichmentPreparedPlans.Store(prepared.Plan, null)));
            var restored = EnrichmentPreparedPlans.Restore(persisted)!;
            Assert.Equal(ipa, restored.SpeechPhonetic);
            Assert.Equal(PronunciationSpeechOutcome.Ready, (await f.Client.GenerateAsync(new(PronunciationPreparationOutcome.Ready, restored), Ct)).Outcome);
            var body = JsonNode.Parse(f.Handler.Bodies[1])!;
            Assert.Equal(word + "/(" + ipa + ")", body["pronunciation_dict"]!["tone"]![0]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("liːd;led", "liːd", "led", null)]
    [InlineData("ˈsteɪb(ə)l", "ˈsteɪbəl", "ˈsteɪbɪl", null)]
    [InlineData("ˈsteɪb((ə))l", "ˈsteɪbəl", "ˈsteɪbəl", null)]
    public async Task ConflictingOrMalformedConstraints_HaveSpecificDiagnosticAndNoSpeech(string required, string echo, string ipa, string? candidate)
    {
        using var f = new Fixture(true);
        f.Handler.Respond = (_, _) => Task.FromResult(IpaCompletion("lead", echo, ipa, candidate));
        var result = await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime).PrepareAsync(NotationRequest("lead", required, AudioLocale.EnUs), Ct);
        Assert.Equal("phonetic_constraint_mismatch", result.ErrorCode); Assert.Null(result.Plan);
        Assert.Equal(PronunciationSpeechOutcome.InvalidPlan, (await f.Client.GenerateAsync(result, Ct)).Outcome);
        Assert.Single(f.Handler.Bodies);
    }

    [Theory]
    [InlineData("accent", "/ˈæksənt/", "ˈæksent", "ˈæksənt")]
    [InlineData("kgs", "ˈkɪləɡræmz", "kɪˈlɒgræmz", "ˈkɪləɡræmz")]
    public async Task ExistingSingleReading_IsBoundLocallyWithoutOverwritingCatalogOrSpendingASecondRequest(
        string word, string required, string invented, string expected)
    {
        using var f = new Fixture(true);
        f.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
            ? IpaCompletion(word, invented, invented, invented) : Native());
        var prepared = await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime)
            .PrepareAsync(NotationRequest(word, required, AudioLocale.EnGb), Ct);
        Assert.Equal(PronunciationPreparationOutcome.Ready, prepared.Outcome);
        Assert.Equal(required, prepared.Plan!.RequiredPhonetic);
        Assert.Null(prepared.Plan.Content.Phonetic);
        Assert.Equal(expected, prepared.Plan.SpeechPhonetic);
        Assert.Equal(PronunciationSpeechOutcome.Ready, (await f.Client.GenerateAsync(prepared, Ct)).Outcome);
        Assert.Equal(2, f.Handler.Bodies.Count);
        Assert.Equal(word + "/(" + expected + ")", JsonNode.Parse(f.Handler.Bodies[1])!["pronunciation_dict"]!["tone"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task NewWord_UsesOneCandidateForFieldsAndSpeechDespiteRedundantModelTranscription()
    {
        using var f = new Fixture(true);
        f.Handler.Respond = (_, _) => Task.FromResult(IpaCompletion("kilograms", "ˈkɪləɡræmz", "ˈkɪlɡræmz", "ˈkɪləɡræmz", kind: "word"));
        var prepared = await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime)
            .PrepareAsync(NotationRequest("kgs", null, AudioLocale.EnGb), Ct);
        Assert.Equal(PronunciationPreparationOutcome.Ready, prepared.Outcome);
        Assert.Equal("ˈkɪləɡræmz", prepared.Plan!.Content.Phonetic);
        Assert.Equal("ˈkɪləɡræmz", prepared.Plan.SpeechPhonetic);
        Assert.Null(prepared.Plan.RequiredPhonetic);
        Assert.Single(f.Handler.Bodies);
    }

    [Theory]
    [InlineData("a lot of/lots of", "a lot of or lots of", "ə lɒt əv ɔː lɒts əv")]
    [InlineData("It took (sb.) some time to do sth.", "It took somebody some time to do something", "ɪt tʊk ˈsʌmbədi səm taɪm tə duː ˈsʌmθɪŋ")]
    public async Task NativeTemplates_HaveWholeExpandedSpeechIpaAndNoWritableCandidates(string word, string spoken, string ipa)
    {
        foreach (var locale in new[] { AudioLocale.EnGb, AudioLocale.EnUs })
        {
            using var f = new Fixture(true);
            f.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? IpaCompletion(spoken, null, ipa) : Native());
            var prepared = await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime).PrepareAsync(NotationRequest(word, null, locale), Ct);
            Assert.Equal(PronunciationPreparationOutcome.Ready, prepared.Outcome);
            Assert.Null(prepared.Plan!.Content.Phonetic); Assert.Empty(prepared.Plan.Content.PartsOfSpeech!);
            Assert.Equal(PronunciationSpeechOutcome.Ready, (await f.Client.GenerateAsync(prepared, Ct)).Outcome);
            var body = JsonNode.Parse(f.Handler.Bodies[1])!;
            Assert.Equal(spoken, body["text"]!.GetValue<string>());
            Assert.Equal(spoken + "/(" + ipa + ")", body["pronunciation_dict"]!["tone"]![0]!.GetValue<string>());
            var proposal = JsonNode.Parse(JsonNode.Parse(f.Handler.Bodies[0])!["messages"]![1]!["content"]!.GetValue<string>())!;
            Assert.Equal("ipa", proposal["speech"]!["requiredControl"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("lead", "lead", "liːd", "instructions", "pronunciation_control_unsupported")]
    [InlineData("a lot of/lots of", "a lot of/lots of", null, "ipa", "template_unexpanded")]
    [InlineData("a lot of/lots of", "a lot of", null, "ipa", "template_unexpanded")]
    [InlineData("a lot of/lots of", "a lot of or a lot of", null, "ipa", "template_unexpanded")]
    [InlineData("It took (sb.) some time to do sth.", "It took (somebody) some time to do something", null, "ipa", "template_unexpanded")]
    [InlineData("give sb. sth.", "give somebody something", "liːd", "ipa", "pronunciation_text_mismatch")]
    public async Task UnsupportedPlans_StopDuringPreparation(string word, string spoken, string? required, string control, string error)
    {
        using var f = new Fixture(true);
        f.Handler.Respond = (_, _) => Task.FromResult(IpaCompletion(spoken, required, "liːd", control: control));
        var client = new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime);
        var request = NotationRequest(word, required, AudioLocale.EnGb);
        var result = await client.PrepareAsync(request, Ct);
        Assert.Equal(error, result.ErrorCode); Assert.Null(result.Plan); Assert.Single(f.Handler.Bodies);
        if (control == "instructions")
        {
            await f.Publish(new() { ["Enrichment:Tts:ApiKey"] = "", ["Enrichment:FieldsEnabled"] = "true" });
            Assert.Equal(PronunciationPreparationOutcome.Ready, (await client.PrepareAsync(request, Ct)).Outcome);
        }
    }
}
