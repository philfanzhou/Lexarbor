using System.Security.Cryptography;
using System.Text.Json;
using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Services;

/// <summary>One input and revision rule shared by every future audio caller.</summary>
public static class AudioSelectionService
{
    public static AudioGenerationInput GetInput(AudioWordInput word, AudioMeaningInput? meaning, AudioLocale locale)
    {
        _ = locale.ToCode();
        return new(word.Word, locale, locale == AudioLocale.EnGb ? word.PhoneticUk : word.PhoneticUs,
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
        var effective = own ?? snapshot.Bindings.SingleOrDefault(binding => binding.Locale == locale
            && binding.MeaningId == null);
        var source = effective == null ? (AudioBindingSource?)null
            : effective.MeaningId == null ? AudioBindingSource.Default : AudioBindingSource.Meaning;
        // A default fallback is compared with DEFAULT input, never with the
        // requested meaning's input. A stale override still wins selection.
        var effectiveRevision = effective?.MeaningId == null
            ? ComputeRevision(GetInput(snapshot.Word, null, locale)) : revision;
        var status = effective == null ? AudioContentStatus.Missing
            : effective.SourceRevision == effectiveRevision ? AudioContentStatus.Ready : AudioContentStatus.Stale;
        return new(locale, input, revision, own, effective, source, status);
    }
}
