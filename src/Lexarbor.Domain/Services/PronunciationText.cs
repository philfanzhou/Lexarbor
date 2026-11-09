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
        // A dotted-initial run that runs straight into the next word separates
        // too: I.M.Pei -> I M Pei, J.R.R.Tolkien -> J R R Tolkien. The boundary
        // rule above deliberately skips that spelling so the two must not merge.
        value = Regex.Replace(value, @"(?<![\p{L}\p{N}])((?:[A-Z]\.){2,})(?=\p{L})",
            match => string.Join(" ", match.Groups[1].Value.Where(char.IsLetter)) + " ", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?<=\p{L})-(?=\p{L})", " ", RegexOptions.CultureInvariant);
        // A leading/trailing dictionary hyphen marks a bound affix, not a
        // spoken punctuation mark. Do not join lexical tokens across hyphens.
        value = Regex.Replace(value, @"(?<!\S)-(?=\p{L})|(?<=\p{L})-(?!\S)", "", RegexOptions.CultureInvariant);
        return Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant);
    }

    public static bool SameSurface(string original, string spoken)
    {
        static string[] Tokens(string value) => Regex.Replace(Normalize(value), @"\b[A-Z]{2,8}\b",
            match => string.Join(" ", match.Value.ToCharArray()), RegexOptions.CultureInvariant).Split(' ');
        var source = Tokens(original);
        var target = Tokens(spoken);
        if (source.Length != target.Length) return false;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == target[i]) continue;
            // The preparation model selects by meaning; existing IPA must also
            // match the complete resulting utterance before this reaches TTS.
            string[] allowed = source[i] switch
            {
                "Dr." => ["doctor"],
                "Drs." => ["doctors"],
                "St." => ["saint", "street"],
                "Mr." => ["mister"],
                "Mrs." => ["missus"],
                "Ms." => ["miz"],
                "cm" or "cm." => ["centimetre", "centimeter"],
                "cms" or "cms." => ["centimetres", "centimeters"],
                "kg" or "kg." => ["kilogram"],
                "kgs" or "kgs." => ["kilograms"],
                "km" or "km." => ["kilometre", "kilometer"],
                "kms" or "kms." => ["kilometres", "kilometers"],
                "mm" or "mm." => ["millimetre", "millimeter"],
                "mms" or "mms." => ["millimetres", "millimeters"],
                _ => []
            };
            if (!allowed.Contains(target[i], StringComparer.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
