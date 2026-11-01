using System.Text;
using System.Text.RegularExpressions;
using Lexarbor.Domain.Models;
using Lexarbor.Host.Configuration;

namespace Lexarbor.Host.Integration;

internal static class SpeechProviderCapabilities
{
    // Deliberate allowlist, not a claim that arbitrary compatible servers or
    // future model aliases implement these pronunciation controls.
    public static bool OpenAiInstructions(string model) => model is "gpt-4o-mini-tts" or "gpt-4o-mini-tts-2025-12-15";
    public static bool OpenAiText(string model) => OpenAiInstructions(model) || model is "tts-1" or "tts-1-hd";
    public static bool MiniMax(string model) => model is "speech-2.8-hd" or "speech-2.8-turbo";
    public static bool OfficialOpenAi(string endpoint) => OfficialBase(endpoint, "api.openai.com");
    public static bool OfficialMiniMax(string endpoint) => OfficialBase(endpoint, "api.minimax.io", "api.minimax.cn");
    private static bool OfficialBase(string endpoint, params string[] hosts) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && hosts.Contains(uri.Host, StringComparer.Ordinal) && uri.IsDefaultPort
        && uri.AbsolutePath.TrimEnd('/') == "/v1" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    public static bool ValidVoice(EnrichmentVoiceSettings voice) => !string.IsNullOrWhiteSpace(voice.Voice)
        && voice.Voice.Length <= 2048 && !voice.Voice.Any(char.IsControl);

    // One exact original/replacement rule covers the entire unchanged spoken
    // target. Never guess word offsets after a template expansion, pass an IPA
    // wrapper as syntax, or allow native pause/interjection/dictionary injection.
    public static string? NativeIpa(PreparedPronunciation plan)
    {
        if (plan.Control != PronunciationControl.Ipa) return null;
        if (plan.RequiredPhonetic != null && plan.Content.SpokenText.Normalize(NormalizationForm.FormC) != plan.Input.Word.Trim().Normalize(NormalizationForm.FormC)) return null;
        var ipa = plan.SpeechPhonetic;
        if (string.IsNullOrWhiteSpace(ipa)) return null;
        ipa = ipa.Normalize(NormalizationForm.FormC).Trim();
        if (ipa.Length >= 2 && (ipa[0] == '/' && ipa[^1] == '/' || ipa[0] == '[' && ipa[^1] == ']')) ipa = ipa[1..^1].Trim();
        return ipa.Length > 0 && ipa is not ("laughs" or "chuckle" or "coughs" or "clear-throat" or "groans" or "breath" or "pant" or "inhale" or "exhale" or "gasps" or "sniffs" or "sighs" or "snorts" or "burps" or "lip-smacking" or "humming" or "hissing" or "emm" or "sneezes")
            && Regex.IsMatch(ipa, @"\A[a-zæøðŋœθβχ\u0250-\u02FF\u0300-\u036F\u1D00-\u1D7F \u00A0.‿↗↘|‖]+\z", RegexOptions.CultureInvariant)
            && !ipa.Any(char.IsControl) ? ipa : null;
    }
}
