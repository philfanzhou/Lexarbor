using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Services;

/// <summary>One input and revision rule shared by every future audio caller.</summary>
public static class AudioSelectionService
{
    public static VocabularyPronunciation GetPronunciation(AudioWordInput word, AudioMeaningInput? meaning)
    {
        VocabularyPronunciationAccent Accent(AudioLocale locale)
        {
            var phonetic = GetInput(word, meaning, locale).Phonetic;
            return new(string.IsNullOrWhiteSpace(phonetic) ? null : phonetic,
                string.IsNullOrWhiteSpace(GetSpecificPhonetic(meaning, locale)) ? "default" : "meaning");
        }
        return new(Accent(AudioLocale.EnGb), Accent(AudioLocale.EnUs));
    }

    public static AudioGenerationInput GetInput(AudioWordInput word, AudioMeaningInput? meaning, AudioLocale locale)
    {
        _ = locale.ToCode();
        var specific = GetSpecificPhonetic(meaning, locale);
        var phonetic = string.IsNullOrWhiteSpace(specific)
            ? locale == AudioLocale.EnGb ? word.PhoneticUk : word.PhoneticUs : specific;
        return new(word.Word, locale, phonetic,
            meaning != null, meaning?.PartOfSpeech, meaning?.Meaning);
    }

    public static string ComputeRevision(AudioGenerationInput input)
    {
        // A fixed array, explicit nulls and a version tag avoid delimiter and
        // null/empty collisions. IDs, examples, positions and timestamps are
        // not spoken input. Never normalize the display spelling here.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            "lexarbor.audio.source.v1", input.Word, input.Locale.ToCode(), input.Phonetic,
            input.IsMeaning ? "meaning" : "default", input.PartOfSpeech, input.Meaning
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static AudioSelection Select(AudioReadSnapshot snapshot, AudioLocale locale)
    {
        var input = GetInput(snapshot.Word, snapshot.Meaning, locale);
        var revision = ComputeRevision(input);
        var own = snapshot.Bindings.SingleOrDefault(binding => binding.Locale == locale
            && binding.MeaningId == snapshot.Meaning?.MeaningId);
        var baseline = snapshot.Bindings.SingleOrDefault(binding => binding.Locale == locale && binding.MeaningId == null);
        var specific = GetSpecificPhonetic(snapshot.Meaning, locale);
        var defaultPhonetic = locale == AudioLocale.EnGb ? snapshot.Word.PhoneticUk : snapshot.Word.PhoneticUs;
        var effective = string.IsNullOrWhiteSpace(specific) ? baseline
            : own ?? (PhoneticsMatch(specific, defaultPhonetic) ? baseline : null);
        var source = effective == null ? (AudioBindingSource?)null
            : effective.MeaningId == null ? AudioBindingSource.Default : AudioBindingSource.Meaning;
        // A default fallback is compared with DEFAULT input, never with the
        // requested meaning's input. An explicit stale override still wins;
        // an inherited target keeps its own binding only for management.
        var effectiveRevision = effective?.MeaningId == null
            ? ComputeRevision(GetInput(snapshot.Word, null, locale)) : revision;
        var status = effective == null ? AudioContentStatus.Missing
            : effective.SourceRevision == effectiveRevision ? AudioContentStatus.Ready : AudioContentStatus.Stale;
        return new(locale, input, revision, own, effective, source, status);
    }

    private static string? GetSpecificPhonetic(AudioMeaningInput? meaning, AudioLocale locale)
        => locale == AudioLocale.EnGb ? meaning?.PhoneticUk : meaning?.PhoneticUs;

    private static bool PhoneticsMatch(string? specific, string? baseline)
    {
        var left = NormalizePhonetic(specific);
        var right = NormalizePhonetic(baseline);
        return left.Length > 0 && right.Length > 0 && string.Equals(left, right, StringComparison.Ordinal);
    }

    public static string NormalizePhonetic(string? phonetic)
    {
        var value = phonetic?.Normalize(NormalizationForm.FormC).Trim() ?? string.Empty;
        if (value.Length >= 2 && (value[0] == '/' && value[^1] == '/' || value[0] == '[' && value[^1] == ']'))
            value = value[1..^1].Trim();
        return value;
    }
}
