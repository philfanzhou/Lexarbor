using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;

namespace Lexarbor.Service.Tests;

public sealed class PronunciationSpeechTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Secret = "synthetic-speech-key";
    private static byte[] Mp3 => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"));
    private static HttpResponseMessage Audio(byte[]? bytes = null) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? Mp3) };
    private static HttpResponseMessage Native(int code = 0, string? hex = null) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { base_resp = new { status_code = code, status_msg = Secret + " raw detail" }, data = new { status = 2, audio = hex ?? Convert.ToHexString(Mp3) } })
    };

    [Theory]
    [InlineData(false, AudioLocale.EnGb)]
    [InlineData(false, AudioLocale.EnUs)]
    [InlineData(true, AudioLocale.EnGb)]
    [InlineData(true, AudioLocale.EnUs)]
    public async Task ProtocolFixtures_MapExplicitHumanReadingAndLocale_WithoutSpeakingDirections(bool native, AudioLocale locale)
    {
        using var f = new Fixture(native);
        var preparation = f.Plan(locale: locale, phonetic: "/lɛd/", meaning: true);
        var result = await f.Client.GenerateAsync(preparation, Ct);
        Assert.Equal(PronunciationSpeechOutcome.Ready, result.Outcome); Assert.Equal(Mp3, result.Audio); Assert.True(result.RequestMade);
        var body = JsonNode.Parse(Assert.Single(f.Handler.Bodies))!;
        Assert.Equal(Secret, Assert.Single(f.Handler.Keys)); Assert.True(f.Handler.Suppressed);
        Assert.Equal(native ? "speech-2.8-hd" : "gpt-4o-mini-tts", body["model"]!.GetValue<string>());
        var voice = locale == AudioLocale.EnGb ? "british-voice" : "american-voice";
        if (native)
        {
            Assert.Equal("lead", body["text"]!.GetValue<string>()); Assert.False(body["stream"]!.GetValue<bool>());
            Assert.Equal("hex", body["output_format"]!.GetValue<string>());
            Assert.Equal(voice, body["voice_setting"]!["voice_id"]!.GetValue<string>());
            Assert.Equal(locale == AudioLocale.EnGb ? 0.9m : 1.1m, body["voice_setting"]!["speed"]!.GetValue<decimal>());
            Assert.Equal("lead/(lɛd)", Assert.Single(body["pronunciation_dict"]!["tone"]!.AsArray())!.GetValue<string>());
            Assert.Equal("mp3", body["audio_setting"]!["format"]!.GetValue<string>());
            Assert.Null(body["instructions"]);
        }
        else
        {
            Assert.Equal("lead", body["input"]!.GetValue<string>()); Assert.Equal(voice, body["voice"]!.GetValue<string>());
            Assert.Equal(locale == AudioLocale.EnGb ? 0.9m : 1.1m, body["speed"]!.GetValue<decimal>());
            Assert.Equal("mp3", body["response_format"]!.GetValue<string>());
            var instructions = body["instructions"]!.GetValue<string>();
            Assert.Contains("/lɛd/", instructions); Assert.DoesNotContain("\\u025B", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(locale == AudioLocale.EnGb ? "British English" : "American English", instructions);
            Assert.Contains("required reading", instructions);
        }
        Assert.Equal(PronunciationSpeechClient.HttpClientName, Assert.Single(f.Factory.Names));
    }

    [Theory]
    [InlineData("tts-1")]
    [InlineData("tts-1-hd")]
    [InlineData("unknown-model")]
    [InlineData("gpt-realtime-2.1-mini")]
    public async Task UnsupportedInstructions_RejectWithoutDispatchOrCallRecord(string model)
    {
        using var f = new Fixture(); await f.Publish(new() { ["Enrichment:Tts:Model"] = model });
        var records = 0;
        var result = await f.Client.GenerateAsync(f.Plan(), Ct, (_, _) => { records++; return Task.FromResult(true); });
        Assert.Equal(PronunciationSpeechOutcome.Rejected, result.Outcome); Assert.Equal("capability_unsupported", result.ErrorCode);
        Assert.False(result.RequestMade); Assert.Null(result.Audio); Assert.Empty(f.Handler.Bodies); Assert.Equal(0, records);
    }

    [Theory]
    [InlineData("https://third-party.example.test/v1")]
    [InlineData("https://api.openai.com.example.test/v1")]
    [InlineData("http://api.openai.com/v1")]
    [InlineData("https://api.openai.com:8443/v1")]
    [InlineData("https://api.openai.com/other/v1")]
    public async Task SameModelNameOnUnknownEndpoint_DoesNotGrantPronunciationCapability(string endpoint)
    {
        using var f = new Fixture(); await f.Publish(new() { ["Enrichment:Tts:Endpoint"] = endpoint });
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal("capability_unsupported", result.ErrorCode); Assert.False(result.RequestMade); Assert.Empty(f.Handler.Bodies);
    }

    [Theory]
    [InlineData("https://third-party.example.test/v1")]
    [InlineData("http://api.minimax.io/v1")]
    [InlineData("https://api.minimax.io.example.test/v1")]
    public async Task UnknownNativeEndpoint_DoesNotGrantPronunciationCapability(string endpoint)
    {
        using var f = new Fixture(true); await f.Publish(new() { ["Enrichment:Tts:Endpoint"] = endpoint });
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal("capability_unsupported", result.ErrorCode); Assert.False(result.RequestMade); Assert.Empty(f.Handler.Bodies);
    }

    [Fact]
    public async Task DocumentedChinaEndpointAndTurboModel_UseSameNativeMapping()
    {
        using var f = new Fixture(true);
        await f.Publish(new() { ["Enrichment:Tts:Endpoint"] = "https://api.minimax.cn/v1", ["Enrichment:Tts:Model"] = "speech-2.8-turbo" });
        Assert.Equal(PronunciationSpeechOutcome.Ready, (await f.Client.GenerateAsync(f.Plan(), Ct)).Outcome);
        Assert.Contains("speech-2.8-turbo", Assert.Single(f.Handler.Bodies));
    }

    [Theory]
    [InlineData("speech-2.6-hd", "lead", "liːd")]
    [InlineData("speech-2.8-hd", "lead", null)]
    [InlineData("speech-2.8-hd", "lead", "liːd)(sighs")]
    [InlineData("speech-2.8-hd", "lead", "https://untrusted.test/audio")]
    [InlineData("speech-2.8-hd", "lead", "[[liːd]]")]
    [InlineData("speech-2.8-hd", "lead", "中文")]
    [InlineData("speech-2.8-hd", "lead", "sighs")]
    [InlineData("speech-2.8-hd", "take somebody", "teɪk")]
    public async Task NativeUnsupportedMappings_StopBeforeHttp(string model, string spoken, string? phonetic)
    {
        using var f = new Fixture(true); await f.Publish(new() { ["Enrichment:Tts:Model"] = model });
        var result = await f.Client.GenerateAsync(f.Plan(spoken: spoken, phonetic: phonetic), Ct);
        Assert.Equal("capability_unsupported", result.ErrorCode); Assert.False(result.RequestMade); Assert.Empty(f.Handler.Bodies);
    }

    [Theory]
    [InlineData(false, "0.1")]
    [InlineData(true, "0.25")]
    [InlineData(true, "4")]
    public async Task ProviderSpeedLimits_AreStricterThanSavableConfiguration(bool native, string speed)
    {
        using var f = new Fixture(native); await f.Publish(new() { ["Enrichment:Tts:Uk:Speed"] = speed });
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal("capability_unsupported", result.ErrorCode); Assert.False(result.RequestMade); Assert.Empty(f.Handler.Bodies);
    }

    [Theory]
    [InlineData(1002, PronunciationSpeechOutcome.Throttled, "rate_limited")]
    [InlineData(429, PronunciationSpeechOutcome.Throttled, "rate_limited")]
    [InlineData(1039, PronunciationSpeechOutcome.Throttled, "rate_limited")]
    [InlineData(1001, PronunciationSpeechOutcome.ServiceFailure, "service_timeout")]
    [InlineData(1024, PronunciationSpeechOutcome.ServiceFailure, "service_unavailable")]
    [InlineData(1004, PronunciationSpeechOutcome.Rejected, "credentials_rejected")]
    [InlineData(2049, PronunciationSpeechOutcome.Rejected, "credentials_rejected")]
    [InlineData(1008, PronunciationSpeechOutcome.Rejected, "balance_required")]
    [InlineData(2013, PronunciationSpeechOutcome.Rejected, "request_rejected")]
    [InlineData(2022, PronunciationSpeechOutcome.Rejected, "capability_unsupported")]
    [InlineData(9999, PronunciationSpeechOutcome.Rejected, "request_rejected")]
    public async Task NativeBusinessErrors_WinOverOtherwiseValidAudio(int code, PronunciationSpeechOutcome outcome, string reason)
    {
        using var f = new Fixture(true); f.Handler.Respond = (_, _) =>
        {
            var response = Native(code); response.Headers.RetryAfter = new(TimeSpan.FromSeconds(900)); return Task.FromResult(response);
        };
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(reason, result.ErrorCode); Assert.True(result.RequestMade); Assert.Null(result.Audio);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result)); Assert.Single(f.Handler.Bodies);
        if (outcome == PronunciationSpeechOutcome.Throttled) Assert.Equal(TimeSpan.FromSeconds(900), result.RetryAfter);
    }

    [Theory]
    [InlineData(false, 429, PronunciationSpeechOutcome.Throttled)]
    [InlineData(true, 429, PronunciationSpeechOutcome.Throttled)]
    [InlineData(false, 503, PronunciationSpeechOutcome.ServiceFailure)]
    [InlineData(true, 500, PronunciationSpeechOutcome.ServiceFailure)]
    [InlineData(false, 408, PronunciationSpeechOutcome.ServiceFailure)]
    [InlineData(true, 401, PronunciationSpeechOutcome.Rejected)]
    [InlineData(false, 403, PronunciationSpeechOutcome.Rejected)]
    [InlineData(true, 400, PronunciationSpeechOutcome.Rejected)]
    [InlineData(false, 302, PronunciationSpeechOutcome.Rejected)]
    public async Task HttpFailures_AreSafeAndNeverRetried(bool native, int status, PronunciationSpeechOutcome outcome)
    {
        using var f = new Fixture(native); f.Handler.Respond = (_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Secret + "raw error") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(720)); return Task.FromResult(response);
        };
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal(outcome, result.Outcome); Assert.True(result.RequestMade); Assert.Null(result.Audio); Assert.Single(f.Handler.Bodies);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(720), result.RetryAfter);
    }

    [Theory]
    [InlineData("credit_balance_exhausted")]
    [InlineData("organization_spend_limit_exceeded")]
    [InlineData("project_spend_limit_exceeded")]
    [InlineData("organization_usage_limit_exceeded")]
    [InlineData("insufficient_quota")]
    public async Task Billing429_IsTerminalRatherThanRecoverableThrottling(string code)
    {
        using var f = new Fixture(); f.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Content = JsonContent.Create(new { error = new { code, type = "insufficient_quota", message = Secret } }) });
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal(PronunciationSpeechOutcome.Rejected, result.Outcome); Assert.Equal("balance_required", result.ErrorCode);
        Assert.True(result.RequestMade); Assert.Null(result.Audio); Assert.Single(f.Handler.Bodies);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("timeout")]
    [InlineData("duplicate-code")]
    public async Task Unreadable429_RemainsThrottledWithoutConsumingAServiceFailure(string scenario)
    {
        using var f = new Fixture(timeout: 1);
        f.Handler.Respond = (_, _) =>
        {
            HttpContent body = scenario switch
            {
                "oversized" => new StreamContent(new UnknownLengthStream(new byte[65537])),
                "timeout" => new StreamContent(new WaitingStream()),
                "duplicate-code" => new StringContent("{\"error\":{\"code\":\"credit_balance_exhausted\",\"code\":\"slow_down\"}}"),
                _ => new StringContent("not json")
            };
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = body };
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(10)); return Task.FromResult(response);
        };
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal(PronunciationSpeechOutcome.Throttled, result.Outcome); Assert.Equal(TimeSpan.FromMinutes(10), result.RetryAfter);
        Assert.True(result.RequestMade); Assert.Null(result.Audio);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterDate_IsPreservedBeyondNormalThrottleCap(bool native)
    {
        using var f = new Fixture(native);
        var now = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        f.Handler.Respond = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(now.AddMinutes(15)); return Task.FromResult(response);
        };
        var client = new PronunciationSpeechClient(f.Factory, f.Runtime, new FixedTime(now));
        Assert.Equal(TimeSpan.FromMinutes(15), (await client.GenerateAsync(f.Plan(), Ct)).RetryAfter);
    }

    [Theory]
    [InlineData("rate_limit_exceeded", PronunciationSpeechOutcome.Throttled)]
    [InlineData("slow_down", PronunciationSpeechOutcome.Throttled)]
    [InlineData("insufficient_quota", PronunciationSpeechOutcome.Rejected)]
    [InlineData("invalid_api_key", PronunciationSpeechOutcome.Rejected)]
    [InlineData("unknown", PronunciationSpeechOutcome.Rejected)]
    public async Task CompatibleBusinessErrors_AreNotAudio(string code, PronunciationSpeechOutcome outcome)
    {
        using var f = new Fixture(); f.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("\r\n" + JsonSerializer.Serialize(new { error = new { code, message = Secret } })) });
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal(outcome, result.Outcome); Assert.Null(result.Audio); Assert.True(result.RequestMade);
    }

    [Theory]
    [InlineData(true, "bad-hex")]
    [InlineData(true, "odd-hex")]
    [InlineData(true, "url")]
    [InlineData(true, "empty")]
    [InlineData(true, "truncated")]
    [InlineData(false, "empty")]
    [InlineData(false, "truncated")]
    [InlineData(false, "not-mp3")]
    [InlineData(false, "one-frame")]
    [InlineData(true, "incomplete")]
    [InlineData(true, "duplicate-status")]
    [InlineData(true, "missing-status")]
    public async Task InvalidSupplierAudio_HasNoUsablePayload(bool native, string scenario)
    {
        using var f = new Fixture(native); f.Handler.Respond = (_, _) => Task.FromResult(scenario switch
        {
            "bad-hex" => Native(hex: "not-hex"),
            "odd-hex" => Native(hex: "FFF"),
            "url" => Native(hex: "https://untrusted.test/audio.mp3"),
            "empty" => native ? Native(hex: "") : Audio([]),
            "truncated" => native ? Native(hex: Convert.ToHexString(Mp3[..^1])) : Audio(Mp3[..^1]),
            "not-mp3" => Audio("fake audio"u8.ToArray()),
            "one-frame" => Audio(SmallFrames()[..26]),
            "incomplete" => new(HttpStatusCode.OK) { Content = new StringContent("{\"base_resp\":{\"status_code\":0},\"data\":{\"status\":1,\"audio\":\"FF\"}}") },
            "duplicate-status" => new(HttpStatusCode.OK) { Content = new StringContent("{\"base_resp\":{\"status_code\":1002,\"status_code\":0}}") },
            _ => new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":null}") }
        });
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal(PronunciationSpeechOutcome.InvalidAudio, result.Outcome); Assert.Null(result.Audio); Assert.Single(f.Handler.Bodies);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResponseLimits_ApplyWithAndWithoutLengthHeader(bool native, bool unknownLength)
    {
        using var f = new Fixture(native); var limit = native ? PronunciationSpeechClient.MaximumJsonBytes : PronunciationSpeechClient.MaximumAudioBytes;
        f.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = unknownLength ? new StreamContent(new UnknownLengthStream(new byte[limit + 1])) : new ByteArrayContent(new byte[limit + 1]) });
        var result = await f.Client.GenerateAsync(f.Plan(), Ct);
        Assert.Equal("response_too_large", result.ErrorCode); Assert.Null(result.Audio); Assert.True(result.RequestMade);
    }

    [Fact]
    public async Task DecodedNativeSizeAndGeneratedMinimum_DoNotChangeManualMp3Validation()
    {
        var small = SmallFrames(); Assert.True(small.Length < 300); Assert.True(Mp3Validation.IsValid(small));
        foreach (var native in new[] { false, true })
        {
            using var f = new Fixture(native); f.Handler.Respond = (_, _) => Task.FromResult(native ? Native(hex: Convert.ToHexString(small)) : Audio(small));
            Assert.Equal("invalid_audio", (await f.Client.GenerateAsync(f.Plan(), Ct)).ErrorCode);
        }
        using var fixture = new Fixture(true);
        fixture.Handler.Respond = (_, _) => Task.FromResult(Native(hex: new string('0', PronunciationSpeechClient.MaximumAudioBytes * 2 + 2)));
        Assert.Equal("response_too_large", (await fixture.Client.GenerateAsync(fixture.Plan(), Ct)).ErrorCode);
    }

    internal static byte[] SmallFrames()
    {
        var bytes = new byte[52]; // two 26-byte MPEG-2 Layer III, 8 kbps / 22050 Hz frames
        foreach (var offset in new[] { 0, 26 }) { bytes[offset] = 255; bytes[offset + 1] = 243; bytes[offset + 2] = 16; }
        return bytes;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutConnectionAndCancellation_CoverDispatchAndBodyRead(bool native)
    {
        using var f = new Fixture(native, timeout: 1);
        f.Handler.Respond = (_, _) => throw new HttpRequestException(Secret);
        Assert.Equal("connection_failed", (await f.Client.GenerateAsync(f.Plan(), Ct)).ErrorCode);
        f.Handler.Respond = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Audio(); };
        Assert.Equal("service_timeout", (await f.Client.GenerateAsync(f.Plan(), Ct)).ErrorCode);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var call = f.Client.GenerateAsync(f.Plan(), cancelled.Token); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        f.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new WaitingStream()) });
        Assert.Equal("service_timeout", (await f.Client.GenerateAsync(f.Plan(), Ct)).ErrorCode);
        using var bodyCancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var bodyCall = f.Client.GenerateAsync(f.Plan(), bodyCancel.Token); bodyCancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bodyCall);
        var count = f.Handler.Bodies.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Client.GenerateAsync(f.Plan(), cancelled.Token));
        Assert.Equal(count, f.Handler.Bodies.Count);
    }

    [Theory]
    [InlineData("not-ready")]
    [InlineData("missing-plan")]
    [InlineData("lost-human-ipa")]
    [InlineData("changed-source")]
    [InlineData("invalid-kind")]
    [InlineData("unexpanded-template")]
    [InlineData("disabled")]
    [InlineData("missing-voice")]
    [InlineData("missing-speed")]
    [InlineData("missing-key")]
    [InlineData("unready-config")]
    [InlineData("stale-text")]
    public async Task LocalInvalidOrWaitingConditions_DoNotDispatch(string scenario)
    {
        using var f = new Fixture(publish: scenario != "unready-config"); var p = f.Plan();
        switch (scenario)
        {
            case "not-ready": p = p with { Outcome = PronunciationPreparationOutcome.Uncertain }; break;
            case "missing-plan": p = p with { Plan = null }; break;
            case "lost-human-ipa": p = p with { Plan = p.Plan! with { RequiredPhonetic = null } }; break;
            case "changed-source": p = p with { Plan = p.Plan! with { InputVersion = p.Plan.InputVersion with { SourceRevision = "other" } } }; break;
            case "invalid-kind": p = p with { Plan = p.Plan! with { EntryKind = "unknown" } }; break;
            case "unexpanded-template": p = p with { Plan = p.Plan! with { Content = p.Plan.Content with { SpokenText = "give sb. sth." } } }; break;
            case "disabled": await f.Publish(new() { ["Enrichment:AudioEnabled"] = "false" }); break;
            case "missing-voice": await f.Publish(new() { ["Enrichment:Tts:Uk:Voice"] = "" }); break;
            case "missing-speed": await f.Publish(new() { ["Enrichment:Tts:Uk:Speed"] = "" }); break;
            case "missing-key": await f.Publish(new() { ["Enrichment:Tts:ApiKey"] = "" }); break;
            case "stale-text": await f.Publish(new() { ["Enrichment:Text:Model"] = "new-text-model" }); break;
        }
        var callbacks = 0; var result = await f.Client.GenerateAsync(p, Ct, (_, _) => { callbacks++; return Task.FromResult(true); });
        Assert.False(result.RequestMade); Assert.Null(result.Audio); Assert.Empty(f.Handler.Bodies); Assert.Equal(0, callbacks);
        Assert.Equal(scenario is "disabled" or "missing-voice" or "missing-speed" or "missing-key" or "unready-config" ? PronunciationSpeechOutcome.Waiting
            : scenario == "stale-text" ? PronunciationSpeechOutcome.Superseded : PronunciationSpeechOutcome.InvalidPlan, result.Outcome);
    }

    [Fact]
    public async Task CallbackDenialAndConfigurationChanges_PreserveCapturedCallAndLocaleVersions()
    {
        using var f = new Fixture(); var plan = f.Plan(); var old = f.Runtime.Current;
        var denied = await f.Client.GenerateAsync(plan, Ct, (dispatch, _) =>
        {
            Assert.Equal(plan.Plan!.InputVersion, dispatch.InputVersion); Assert.Equal(old.TextVersion, dispatch.TextConfigurationVersion);
            Assert.Equal(old.UkTtsVersion, dispatch.TtsConfigurationVersion); return Task.FromResult(false);
        });
        Assert.Equal("dispatch_rejected", denied.ErrorCode); Assert.Empty(f.Handler.Bodies);
        var granted = await f.Client.GenerateAsync(plan, Ct, async (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            await f.Publish(new() { ["Enrichment:Tts:ApiKey"] = "synthetic-next-key", ["Enrichment:Tts:Uk:Voice"] = "later-voice" }); return true;
        });
        Assert.Equal(PronunciationSpeechOutcome.Ready, granted.Outcome); Assert.Equal(Secret, f.Handler.Keys[0]);
        Assert.Contains("british-voice", f.Handler.Bodies[0]);
        await f.Client.GenerateAsync(plan, Ct); Assert.Equal("synthetic-next-key", f.Handler.Keys[1]); Assert.Contains("later-voice", f.Handler.Bodies[1]);
        Assert.NotEqual(old.UkTtsVersion, f.Runtime.Current.UkTtsVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Client.GenerateAsync(plan, Ct, (_, _) => throw new InvalidOperationException("synthetic-persistence-failure")));
        Assert.Equal(2, f.Handler.Bodies.Count);
    }

    [Fact]
    public async Task InFlightCallAndOtherLocaleConfiguration_AreIndependent()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.Respond = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Audio(); };
        var old = f.Runtime.Current;
        var first = f.Client.GenerateAsync(f.Plan(), Ct); await entered.Task.WaitAsync(Ct);
        await f.Publish(new() { ["Enrichment:Tts:Us:Voice"] = "next-american" });
        Assert.Equal(old.UkTtsVersion, f.Runtime.Current.UkTtsVersion); Assert.NotEqual(old.UsTtsVersion, f.Runtime.Current.UsTtsVersion);
        release.TrySetResult(); Assert.Equal(PronunciationSpeechOutcome.Ready, (await first).Outcome);
        await f.Client.GenerateAsync(f.Plan(locale: AudioLocale.EnUs), Ct);
        Assert.Contains("british-voice", f.Handler.Bodies[0]); Assert.DoesNotContain("next-american", f.Handler.Bodies[0]);
        Assert.Contains("next-american", f.Handler.Bodies[1]);
    }

    [Fact]
    public async Task CompatibleExpandedTemplateAndProperName_KeepTextSeparateFromPronunciationInstructions()
    {
        using var f = new Fixture();
        var plan = f.Plan(spoken: "give somebody something", phonetic: null);
        Assert.Equal(PronunciationSpeechOutcome.Ready, (await f.Client.GenerateAsync(plan, Ct)).Outcome);
        var body = JsonNode.Parse(f.Handler.Bodies[0])!; Assert.Equal("give somebody something", body["input"]!.GetValue<string>());
        Assert.DoesNotContain("required reading", body["input"]!.GetValue<string>());
        await f.Client.GenerateAsync(f.Plan(word: "Nobel", spoken: "Nobel", phonetic: "nəʊˈbel"), Ct);
        Assert.Contains("Nobel", f.Handler.Bodies[1]);
    }

    [Fact]
    public async Task AudioOnlyPhrase_PlannerIpaModeFeedsNativeDictionary_WithoutFieldCandidates()
    {
        using var f = new Fixture(true);
        const string spoken = "give somebody something", ipa = "ɡɪv ˈsʌmbədi ˈsʌmθɪŋ";
        f.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(new
                {
                    certain = true, entryKind = "phrase", classificationBasis = "Explicit phrase position.", spokenText = spoken,
                    pronunciationHint = ipa, requiredPhonetic = (string?)null, phonetic = (string?)null, partsOfSpeech = Array.Empty<object>(),
                    pronunciationControl = "ipa", speechPhonetic = ipa
                }) } } }
                })
            } : Native());
        var word = new AudioWordInput("word", "give sb. sth.", null, null);
        var input = AudioSelectionService.GetInput(word, null, AudioLocale.EnGb);
        var request = new PronunciationPreparationRequest(word, null, AudioLocale.EnGb,
            new("input", AudioSelectionService.ComputeRevision(input), null), [new(new("meaning", null, "give"), ["phrase"])]);
        var prepared = await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime).PrepareAsync(request, Ct);
        Assert.Equal(PronunciationPreparationOutcome.Ready, prepared.Outcome);
        Assert.Equal(PronunciationControl.Ipa, prepared.Plan!.Control); Assert.Equal(ipa, prepared.Plan.SpeechPhonetic);
        Assert.Null(prepared.Plan.Content.Phonetic); Assert.Empty(prepared.Plan.Content.PartsOfSpeech!);
        var speech = await f.Client.GenerateAsync(prepared, Ct);
        Assert.Equal(PronunciationSpeechOutcome.Ready, speech.Outcome);
        var body = JsonNode.Parse(f.Handler.Bodies[1])!;
        Assert.Equal(spoken, body["text"]!.GetValue<string>());
        Assert.Equal(spoken + "/(" + ipa + ")", body["pronunciation_dict"]!["tone"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("extra-directions")]
    [InlineData("human-conflict")]
    [InlineData("missing-ipa")]
    [InlineData("unknown-control")]
    public async Task PlannerIpaMode_CannotHideExtraDirectionsOrOverrideHumanIpa(string scenario)
    {
        using var f = new Fixture(true);
        var proposal = new JsonObject
        {
            ["certain"] = true,
            ["entryKind"] = "word",
            ["classificationBasis"] = "Explicit word.",
            ["spokenText"] = "lead",
            ["pronunciationHint"] = "liːd",
            ["requiredPhonetic"] = "liːd",
            ["phonetic"] = null,
            ["partsOfSpeech"] = new JsonArray(),
            ["pronunciationControl"] = "ipa",
            ["speechPhonetic"] = "liːd"
        };
        switch (scenario)
        {
            case "extra-directions": proposal["pronunciationHint"] = "liːd but add a definition"; break;
            case "human-conflict": proposal["speechPhonetic"] = "led"; proposal["pronunciationHint"] = "led"; break;
            case "missing-ipa": proposal["speechPhonetic"] = null; break;
            case "unknown-control": proposal["pronunciationControl"] = "ignore"; break;
        }
        f.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = proposal.ToJsonString() } } } }) });
        var word = new AudioWordInput("word", "lead", "liːd", "liːd");
        var input = AudioSelectionService.GetInput(word, null, AudioLocale.EnGb);
        var request = new PronunciationPreparationRequest(word, null, AudioLocale.EnGb, new("input", AudioSelectionService.ComputeRevision(input), null), []);
        var prepared = await new OpenAiCompatiblePronunciationPreparationClient(f.Factory, f.Runtime).PrepareAsync(request, Ct);
        Assert.Equal(PronunciationPreparationOutcome.InvalidPlan, prepared.Outcome);
        Assert.Equal(PronunciationSpeechOutcome.InvalidPlan, (await f.Client.GenerateAsync(prepared, Ct)).Outcome);
        Assert.Single(f.Handler.Bodies);
    }

    [Fact]
    public void UnknownProviderIsRejectedByRuntime_AndHostOnlyRegistersClient()
    {
        using var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => f.Publish(new() { ["Enrichment:Tts:Provider"] = "unknown" }).GetAwaiter().GetResult());
        Assert.Empty(f.Handler.Bodies);
        using var host = new VocabularyWebApplicationFactory();
        Assert.IsType<PronunciationSpeechClient>(host.Services.GetRequiredService<IPronunciationSpeechClient>());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = VocabularyWebApplicationFactory.CreateGateSafeDirectory("speech-" + Guid.NewGuid().ToString("N"));
        private readonly Dictionary<string, string?> _settings;
        private readonly List<IDisposable> _configurations = [];
        private readonly PersistentConfigurationCoordinator _coordinator;
        public Handler Handler { get; } = new();
        public Factory Factory { get; }
        public EnrichmentRuntimeConfiguration Runtime { get; }
        public PronunciationSpeechClient Client { get; }
        public Fixture(bool native = false, int timeout = 30, bool publish = true)
        {
            _settings = new()
            {
                ["Enrichment:AudioEnabled"] = "true",
                ["Enrichment:Text:Endpoint"] = "https://planner.example.test/v1",
                ["Enrichment:Text:Model"] = "planner-model",
                ["Enrichment:Text:ApiKey"] = "synthetic-planner-key",
                ["Enrichment:Tts:Provider"] = native ? "MiniMaxNative" : "OpenAICompatible",
                ["Enrichment:Tts:Endpoint"] = native ? "https://api.minimax.io/v1" : "https://api.openai.com/v1",
                ["Enrichment:Tts:Model"] = native ? "speech-2.8-hd" : "gpt-4o-mini-tts",
                ["Enrichment:Tts:ApiKey"] = Secret,
                ["Enrichment:Tts:TimeoutSeconds"] = timeout.ToString(),
                ["Enrichment:Tts:Uk:Voice"] = "british-voice",
                ["Enrichment:Tts:Us:Voice"] = "american-voice",
                ["Enrichment:Tts:Uk:Speed"] = "0.9",
                ["Enrichment:Tts:Us:Speed"] = "1.1"
            };
            var store = new PersistentConfigurationFileStore(_root);
            Runtime = new(Build(), store);
            var coordinatorConfiguration = new ConfigurationBuilder().Build(); _configurations.Add((IDisposable)coordinatorConfiguration);
            _coordinator = new(coordinatorConfiguration, store);
            if (publish) Publish([]).GetAwaiter().GetResult();
            Handler.Respond = (_, _) => Task.FromResult(native ? Native() : Audio());
            Factory = new(Handler); Client = new(Factory, Runtime);
        }
        public PronunciationPreparationResult Plan(string word = "lead", string spoken = "lead", string? phonetic = "liːd", AudioLocale locale = AudioLocale.EnGb, bool meaning = false)
        {
            var input = new AudioGenerationInput(word, locale, phonetic, meaning, meaning ? "n." : null, meaning ? "a metal" : null);
            var native = _settings["Enrichment:Tts:Provider"] == "MiniMaxNative" && phonetic != null;
            return new(PronunciationPreparationOutcome.Ready, new(new(spoken, native ? phonetic : "Use the required reading and accent.", null, []), input,
                new("input-version", AudioSelectionService.ComputeRevision(input), null), Runtime.Current.TextVersion ?? "unready", "word", "Explicit classification.", phonetic,
                native ? phonetic : null, native ? PronunciationControl.Ipa : PronunciationControl.Instructions));
        }
        private IConfigurationRoot Build() { var configuration = new ConfigurationBuilder().AddInMemoryCollection(_settings).Build(); _configurations.Add((IDisposable)configuration); return configuration; }
        public async Task Publish(Dictionary<string, string?> changes)
        {
            foreach (var change in changes) _settings[change.Key] = change.Value;
            // Use the real atomic publisher so unchanged text/other-locale
            // versions stay stable across TTS-only changes.
            var fields = _settings.ToDictionary(p => p.Key, p => p.Key.EndsWith("Enabled", StringComparison.Ordinal)
                ? JsonValue.Create(bool.Parse(p.Value!)) as JsonNode
                : p.Key.EndsWith("TimeoutSeconds", StringComparison.Ordinal) ? JsonValue.Create(int.Parse(p.Value!, System.Globalization.CultureInfo.InvariantCulture))
                : p.Key.EndsWith("Speed", StringComparison.Ordinal) ? string.IsNullOrEmpty(p.Value) ? null : JsonValue.Create(decimal.Parse(p.Value, System.Globalization.CultureInfo.InvariantCulture))
                : JsonValue.Create(p.Value));
            var saved = await _coordinator.SaveAsync((await _coordinator.ReadAsync(Ct)).Revision, fields, Runtime.PrepareAsync, Ct);
            if (saved.Status != ConfigurationSaveStatus.Success) throw new InvalidOperationException("synthetic-invalid-configuration");
        }
        public void Dispose() { Factory.Dispose(); foreach (var configuration in _configurations) configuration.Dispose(); Directory.Delete(_root, true); }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        public List<string> Names { get; } = [];
        public HttpClient CreateClient(string name) { Names.Add(name); return _client; }
        public void Dispose() => _client.Dispose();
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = []; public List<string> Keys { get; } = []; public bool Suppressed;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(Audio());
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Contains(request.RequestUri!.ToString(), new[] { "https://api.minimax.io/v1/t2a_v2", "https://api.minimax.cn/v1/t2a_v2", "https://api.openai.com/v1/audio/speech", "https://planner.example.test/v1/chat/completions" });
            Suppressed = Sdk.SuppressInstrumentation; Keys.Add(request.Headers.Authorization!.Parameter!);
            Bodies.Add(await request.Content!.ReadAsStringAsync(token)); return await Respond(request, token);
        }
    }
    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
