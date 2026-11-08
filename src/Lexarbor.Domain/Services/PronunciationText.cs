using System.Text;
using System.Text.RegularExpressions;

namespace Lexarbor.Domain.Services;

/// <summary>Speakable typography without changing catalog spelling or apostrophe meaning.</summary>
public static class PronunciationText
{
    public static string Normalize(string text)
    {
        var value = text.Normalize(NormalizationForm.FormC).Trim()
            .Replace('\u2018', '\'').Replace('\u2019', '\'')
            .Replace('\u2010', '-').Replace('\u2011', '-').Replace('\u2013', '-').Replace('\u2014', '-');
        // Initials remain separate letters; never turn U.S. into the word "us".
        value = Regex.Replace(value, @"(?<![\p{L}\p{N}])(?:[A-Z]\.){2,}(?![\p{L}\p{N}])",
            match => string.Join(" ", match.Value.Where(char.IsLetter)), RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?<=\p{L})-(?=\p{L})", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant);
    }

    public static bool SameSurface(string original, string spoken)
    {
        var source = Normalize(original).Split(' ');
        var target = Normalize(spoken).Split(' ');
        if (source.Length != target.Length) return false;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == target[i]) continue;
            // The preparation model selects by meaning; existing IPA must also
            // match the complete resulting utterance before this reaches TTS.
            string[] allowed = source[i] switch
            {
                "Dr." => ["doctor"],
                "St." => ["saint", "street"],
                "Mr." => ["mister"],
                "Mrs." => ["missus"],
                "Ms." => ["miz"],
                _ => []
            };
            if (!allowed.Contains(target[i], StringComparer.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
