using System.Text;

namespace Lexarbor.Domain.Services;

/// <summary>Bounded dictionary notation for generation only, never playback equality/revisions.</summary>
public static class PronunciationPhonetics
{
    private const int MaximumVariants = 64;

    public static bool Allows(string notation, string reading) => Concrete(reading) is { } candidate
        && Readings(notation) is { } readings && readings.Contains(candidate, StringComparer.Ordinal);

    public static bool ShareReading(params string[] notations)
    {
        HashSet<string>? common = null;
        foreach (var notation in notations)
        {
            if (Readings(notation) is not { } readings) return false;
            if (common == null) common = new(readings, StringComparer.Ordinal);
            else common.IntersectWith(readings);
        }
        return common is { Count: > 0 };
    }

    public static string? Concrete(string value)
    {
        var clean = Unwrap(value);
        return clean is { Length: > 0 } && clean.IndexOfAny([';', '(', ')', '/', '[', ']']) < 0 ? clean : null;
    }

    private static string? Unwrap(string value)
    {
        if (value.Length > 512 || value.Any(char.IsControl)) return null;
        var clean = value.Normalize(NormalizationForm.FormC).Trim();
        if (clean.Length >= 2 && (clean[0] == '/' && clean[^1] == '/' || clean[0] == '[' && clean[^1] == ']'))
            clean = clean[1..^1].Trim();
        return clean;
    }

    private static string[]? Readings(string notation)
    {
        if (notation.Length > 512) return null;
        // Accept a wrapper around the whole alternatives list as well as one
        // wrapper per alternative, without stripping crossed/nested wrappers.
        var whole = Unwrap(notation);
        if (whole == null) return null;
        if (whole.IndexOfAny(['/', '[', ']']) < 0) notation = whole;
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alternative in notation.Split(';'))
        {
            var value = Unwrap(alternative);
            if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['/', '[', ']']) >= 0) return null;
            var variants = new List<string> { "" };
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == ')') return null;
                if (value[i] != '(')
                { for (var j = 0; j < variants.Count; j++) variants[j] += value[i]; continue; }
                var end = value.IndexOf(')', i + 1);
                if (end < 0) return null;
                var optional = value[(i + 1)..end];
                // Only phonetic letters/marks may be optional, not nested syntax,
                // empty groups, commentary, stress directions or separators.
                if (optional.Length == 0 || optional.Any(c => !char.IsLetter(c)
                    && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)) return null;
                if (variants.Count * 2 > MaximumVariants) return null;
                variants.AddRange(variants.Select(v => v + optional).ToArray());
                i = end;
            }
            foreach (var variant in variants)
            {
                if (Concrete(variant) is not { } concrete) return null;
                result.Add(concrete);
                if (result.Count > MaximumVariants) return null;
            }
        }
        return result.ToArray();
    }
}
