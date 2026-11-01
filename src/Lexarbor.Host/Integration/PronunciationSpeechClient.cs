using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Service;
using OpenTelemetry;

namespace Lexarbor.Host.Integration;

/// <summary>Bounded, single-dispatch speech adapters. No storage or queue IO.</summary>
public sealed class PronunciationSpeechClient(IHttpClientFactory clients, EnrichmentRuntimeConfiguration runtime,
    TimeProvider? timeProvider = null) : IPronunciationSpeechClient
{
    public const string HttpClientName = "PronunciationSpeech";
    public const int MaximumAudioBytes = 5 * 1024 * 1024;
    public const int MaximumJsonBytes = MaximumAudioBytes * 2 + 65536;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<PronunciationSpeechResult> GenerateAsync(PronunciationPreparationResult preparation,
        CancellationToken cancellationToken = default,
        Func<PronunciationSpeechDispatch, CancellationToken, Task<bool>>? beforeDispatch = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preparation.Outcome != PronunciationPreparationOutcome.Ready || preparation.Plan is not { } plan || !ValidPlan(plan))
            return Failure(PronunciationSpeechOutcome.InvalidPlan, "prepared_plan_required");
        var snapshot = runtime.Current;
        if (!snapshot.Enrichment.AudioEnabled) return Failure(PronunciationSpeechOutcome.Waiting, "disabled");
        var locale = plan.Input.Locale == AudioLocale.EnGb ? "uk" : "us";
        if (!snapshot.HasTtsConfiguration(locale)) return Failure(PronunciationSpeechOutcome.Waiting, "configuration_missing");
        if (plan.ConfigurationVersion != snapshot.TextVersion) return Failure(PronunciationSpeechOutcome.Superseded, "preparation_changed");
        var settings = snapshot.Enrichment.Tts;
        var voice = locale == "uk" ? snapshot.Enrichment.Uk : snapshot.Enrichment.Us;
        if (!SpeechProviderCapabilities.ValidVoice(voice)) return Failure(PronunciationSpeechOutcome.Rejected, "parameters_rejected");
        var native = snapshot.Enrichment.Provider == "MiniMaxNative";
        var body = snapshot.Enrichment.Provider switch
        {
            "MiniMaxNative" => MiniMaxSpeechAdapter.Request(plan, settings, voice),
            "OpenAICompatible" => OpenAiCompatibleSpeechAdapter.Request(plan, settings, voice),
            _ => null
        };
        if (body == null) return Failure(PronunciationSpeechOutcome.Rejected, "capability_unsupported");
        if (body.Length > 65536) return Failure(PronunciationSpeechOutcome.InvalidPlan, "request_too_large");
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint.TrimEnd('/') + (native ? "/t2a_v2" : "/audio/speech"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        message.Content = new ByteArrayContent(body);
        message.Content.Headers.ContentType = new("application/json");
        var client = clients.CreateClient(HttpClientName);
        var dispatch = new PronunciationSpeechDispatch(plan.InputVersion, plan.ConfigurationVersion,
            (locale == "uk" ? snapshot.UkTtsVersion : snapshot.UsTtsVersion)!);
        if (beforeDispatch != null && !await beforeDispatch(dispatch, cancellationToken))
            return Failure(PronunciationSpeechOutcome.Superseded, "dispatch_rejected");
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var suppression = SuppressInstrumentationScope.Begin();
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var retry = RetryAfter(response);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return Failure(PronunciationSpeechOutcome.Throttled, "rate_limited", true, retry);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                return Failure(PronunciationSpeechOutcome.ServiceFailure, "service_unavailable", true);
            if (!response.IsSuccessStatusCode) return Failure(PronunciationSpeechOutcome.Rejected,
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "credentials_rejected" : "request_rejected", true);
            var bytes = await ReadBoundedAsync(response.Content, native ? MaximumJsonBytes : MaximumAudioBytes, timeout.Token);
            if (bytes == null) return Failure(PronunciationSpeechOutcome.InvalidAudio, "response_too_large", true);
            var result = native ? MiniMaxSpeechAdapter.Decode(bytes, retry) : OpenAiCompatibleSpeechAdapter.Decode(bytes, retry);
            if (result.Audio is { } audio && audio.AsSpan().IndexOf(Encoding.UTF8.GetBytes(settings.ApiKey)) >= 0)
                return Failure(PronunciationSpeechOutcome.InvalidAudio, "invalid_audio", true);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Failure(PronunciationSpeechOutcome.ServiceFailure, "service_timeout", true); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { return Failure(PronunciationSpeechOutcome.ServiceFailure, "connection_failed", true); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { return Failure(PronunciationSpeechOutcome.InvalidAudio, "invalid_response", true); }
    }

    private static bool ValidPlan(PreparedPronunciation plan) => plan.Input != null && Enum.IsDefined(plan.Input.Locale)
        && plan.Content != null && plan.InputVersion != null && Bounded(plan.Content.SpokenText, 4096)
        && Bounded(plan.Input.Word, 4096) && Bounded(plan.InputVersion.Revision, 128)
        && Bounded(plan.ConfigurationVersion, 128) && Bounded(plan.ClassificationBasis, 512) && plan.EntryKind is "word" or "phrase"
        && Optional(plan.RequiredPhonetic, 512) && Optional(plan.Content.Phonetic, 512) && Optional(plan.Content.PronunciationHint, 2048)
        && Optional(plan.SpeechPhonetic, 512) && Enum.IsDefined(plan.Control)
        && (plan.Control == PronunciationControl.Instructions ? plan.SpeechPhonetic == null
            : Bounded(plan.SpeechPhonetic, 512) && plan.Content.PronunciationHint == plan.SpeechPhonetic
                && (Clean(plan.RequiredPhonetic) == null || Clean(plan.RequiredPhonetic) == plan.SpeechPhonetic)
                && (Clean(plan.Content.Phonetic) == null || Clean(plan.Content.Phonetic) == plan.SpeechPhonetic))
        && Clean(plan.RequiredPhonetic) == Clean(plan.Input.Phonetic)
        && (Clean(plan.RequiredPhonetic) == null || Clean(plan.Content.Phonetic) == null || Clean(plan.RequiredPhonetic) == Clean(plan.Content.Phonetic))
        && plan.InputVersion.SourceRevision == AudioSelectionService.ComputeRevision(plan.Input)
        && !plan.Content.SpokenText.Contains('/') && !Regex.IsMatch(plan.Content.SpokenText, @"\b(?:sb|sth)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool Bounded(string? text, int length) => !string.IsNullOrWhiteSpace(text) && text.Length <= length && !text.Any(char.IsControl);
    private static bool Optional(string? text, int length) => text == null || text.Length <= length && !text.Any(char.IsControl);
    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    internal static bool UniqueProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == element.EnumerateObject().Count()
            && element.EnumerateObject().All(p => UniqueProperties(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(UniqueProperties),
        _ => true
    };
    internal static PronunciationSpeechResult ValidateAudio(byte[] bytes) => bytes.Length is >= 300 and <= MaximumAudioBytes && Mp3Validation.IsValid(bytes)
        ? new(PronunciationSpeechOutcome.Ready, bytes, RequestMade: true)
        : Failure(PronunciationSpeechOutcome.InvalidAudio, "invalid_audio", true);
    internal static PronunciationSpeechResult Failure(PronunciationSpeechOutcome outcome, string code, bool called = false, TimeSpan? retry = null)
        => new(outcome, ErrorCode: code, RequestMade: called, RetryAfter: retry);
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - _time.GetUtcNow() : null);
        return delay is { } value && value > TimeSpan.Zero ? value : null;
    }
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) return null;
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length)), token);
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > maximum) return null;
        }
    }
}
