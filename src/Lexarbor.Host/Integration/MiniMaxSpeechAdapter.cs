using System.Text.Json;
using Lexarbor.Domain.Models;
using Lexarbor.Host.Configuration;

namespace Lexarbor.Host.Integration;

internal static class MiniMaxSpeechAdapter
{
    public static byte[]? Request(PreparedPronunciation plan, EnrichmentClientSettings settings, EnrichmentVoiceSettings voice)
    {
        if (!SpeechProviderCapabilities.OfficialMiniMax(settings.Endpoint) || !SpeechProviderCapabilities.MiniMax(settings.Model) || voice.Speed is not (>= 0.5m and <= 2m)
            || plan.Content.SpokenText.IndexOfAny(['/', '(', ')', '<', '>', '[', ']']) >= 0) return null;
        var ipa = SpeechProviderCapabilities.NativeIpa(plan);
        if (ipa == null) return null;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = settings.Model,
            text = plan.Content.SpokenText,
            stream = false,
            output_format = "hex",
            language_boost = "English",
            voice_setting = new { voice_id = voice.Voice, speed = voice.Speed },
            pronunciation_dict = new { tone = new[] { plan.Content.SpokenText + "/(" + ipa + ")" } },
            audio_setting = new { format = "mp3", sample_rate = 32000, bitrate = 128000, channel = 1 }
        });
    }

    public static PronunciationSpeechResult Decode(byte[] bytes, TimeSpan? retry)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (!PronunciationSpeechClient.UniqueProperties(root)
            || !root.TryGetProperty("base_resp", out var status) || !status.TryGetProperty("status_code", out var code) || !code.TryGetInt32(out var number))
            return PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.InvalidAudio, "invalid_response", true);
        if (number != 0) return BusinessFailure(number, retry);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("status", out var completed) || !completed.TryGetInt32(out var state) || state != 2
            || !data.TryGetProperty("audio", out var audio) || audio.ValueKind != JsonValueKind.String)
            return PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.InvalidAudio, "invalid_response", true);
        var hex = audio.GetString()!;
        if (hex.Length > PronunciationSpeechClient.MaximumAudioBytes * 2)
            return PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.InvalidAudio, "response_too_large", true);
        // Convert validates both parity and every digit; URLs are never fetched.
        return PronunciationSpeechClient.ValidateAudio(Convert.FromHexString(hex));
    }

    private static PronunciationSpeechResult BusinessFailure(int code, TimeSpan? retry) => code switch
    {
        429 or 1002 or 1039 or 1041 or 1045 or 2045 or 2046 or 2047 => PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.Throttled, "rate_limited", true, retry),
        1001 => PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.ServiceFailure, "service_timeout", true),
        1000 or 1024 or 1033 => PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.ServiceFailure, "service_unavailable", true),
        1004 or 2049 => PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.Rejected, "credentials_rejected", true),
        1008 or 2053 => PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.Rejected, "balance_required", true),
        2022 or 2043 => PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.Rejected, "capability_unsupported", true),
        _ => PronunciationSpeechClient.Failure(PronunciationSpeechOutcome.Rejected, "request_rejected", true)
    };
}
