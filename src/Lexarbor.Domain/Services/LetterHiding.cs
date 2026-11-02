using System.Text;

namespace Lexarbor.Domain.Services;

public static class LetterHiding
{
    public static bool IsEligible(string word)
    {
        var (letters, hideable) = Positions(word);
        return letters >= 3 && hideable.Count > 0;
    }

    public static List<string?> Create(string word, Func<int, int> pickIndex)
    {
        var (letters, hideable) = Positions(word);
        if (letters < 3 || hideable.Count == 0)
            throw new ArgumentException("The word has no eligible letter pattern.", nameof(word));

        var pattern = word.EnumerateRunes().Select(rune => (string?)rune.ToString()).ToList();
        var count = Math.Clamp((int)Math.Round(letters * 0.4, MidpointRounding.AwayFromZero), 1, hideable.Count);
        // Partial Fisher-Yates samples a uniform subset without replacement.
        for (var index = 0; index < count; index++)
        {
            var selected = index + pickIndex(hideable.Count - index);
            (hideable[index], hideable[selected]) = (hideable[selected], hideable[index]);
            pattern[hideable[index]] = null;
        }
        return pattern;
    }

    private static (int Letters, List<int> Hideable) Positions(string word)
    {
        var letters = 0;
        var position = 0;
        var firstLetter = true;
        List<int> hideable = [];
        foreach (var rune in word.EnumerateRunes())
        {
            if (rune.Value is ' ' or '-') firstLetter = true;
            else if (Rune.IsLetter(rune))
            {
                letters++;
                if (!firstLetter) hideable.Add(position);
                firstLetter = false;
            }
            position++;
        }
        return (letters, hideable);
    }
}
