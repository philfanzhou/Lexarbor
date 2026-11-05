using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;

namespace Lexarbor.Host.Integration;

internal static class SpeechPreviewSamples
{
    // Fixed samples bypass AI preparation only for previews; no catalog plan is created.
    public static PreparedPronunciation Plan(AudioLocale locale, string sample, string provider)
    {
        if (!Enum.IsDefined(locale) || sample is not ("word" or "sentence")) throw new ArgumentException("Invalid speech sample.");
        var text = sample == "word" ? "apple" : "Welcome to our English lesson.";
        var ipa = sample == "word" ? "ˈæpəl" : locale == AudioLocale.EnGb
            ? "ˈwelkəm tə aʊə ˈɪŋɡlɪʃ ˈlesən" : "ˈwɛlkəm tə aʊɚ ˈɪŋɡlɪʃ ˈlɛsən";
        var native = provider == "MiniMaxNative";
        var input = new AudioGenerationInput(text, locale, null, false, null, null);
        return new(new(text, native ? ipa : null, null, []), input,
            new("preview", AudioSelectionService.ComputeRevision(input), null), "preview", sample == "word" ? "word" : "phrase",
            "Fixed administrator preview sample.", null, native ? ipa : null,
            native ? PronunciationControl.Ipa : PronunciationControl.Instructions);
    }
}
