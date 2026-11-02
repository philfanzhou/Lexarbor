using System.Text;
using Lexarbor.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class LetterHidingTests
{
    [Theory]
    [InlineData("go")]
    [InlineData("I")]
    [InlineData("a-b c")]
    [InlineData("123-😀")]
    [InlineData("𐐀𐐨")]
    public void IneligibleWordsHaveNoPattern(string word)
    {
        Assert.False(LetterHiding.IsEligible(word));
        Assert.Throws<ArgumentException>(() => LetterHiding.Create(word, _ => 0));
    }

    [Theory]
    [InlineData("explode")]
    [InlineData("look after")]
    [InlineData("well-known")]
    [InlineData("'ab-cd e'f")]
    [InlineData("e\u0301cole")]
    [InlineData("𐐀𐐁𐐂-𐐃𐐄𐐅")]
    [InlineData("a-bcd")]
    public void ManySeedsPreserveEveryScalarAndApplyTheCountAndSegmentRules(string word)
    {
        var scalars = word.EnumerateRunes().ToArray();
        List<int> hideable = [];
        var letters = 0;
        var first = true;
        for (var index = 0; index < scalars.Length; index++)
        {
            if (scalars[index].Value is ' ' or '-') first = true;
            else if (Rune.IsLetter(scalars[index]))
            {
                letters++;
                if (!first) hideable.Add(index);
                first = false;
            }
        }
        var expected = Math.Min(hideable.Count, Math.Max(1, (int)Math.Round(letters * 0.4, MidpointRounding.AwayFromZero)));
        HashSet<int> observedBlanks = [];
        for (var seed = 0; seed < 1000; seed++)
        {
            var random = new Random(seed);
            var pattern = LetterHiding.Create(word, random.Next);
            Assert.Equal(scalars.Length, pattern.Count);
            Assert.Equal(expected, pattern.Count(value => value is null));
            Assert.Contains(pattern, value => value is null);
            for (var index = 0; index < scalars.Length; index++)
            {
                if (pattern[index] is null)
                {
                    Assert.Contains(index, hideable);
                    observedBlanks.Add(index);
                }
                else Assert.Equal(scalars[index].ToString(), pattern[index]);
            }
        }
        Assert.Equal(hideable.Order(), observedBlanks.Order());
    }

    [Fact]
    public void ExplodeHidesExactlyThreeLetters()
    {
        Assert.Equal(3, LetterHiding.Create("explode", _ => 0).Count(value => value is null));
    }

    [Fact]
    public void OnlyAsciiSpaceAndHyphenStartANewSegment()
    {
        Assert.Equal(new string?[] { "a", "\t", null, "\u00a0", "c" }, LetterHiding.Create("a\tb\u00a0c", _ => 0));
    }

    [Fact]
    public void UniformPickerMakesEveryAllowedSubsetEquallyReachable()
    {
        // Four letters hide two of three candidates: all six ordered draws have equal weight.
        Dictionary<string, int> counts = [];
        for (var first = 0; first < 3; first++)
            for (var second = 0; second < 2; second++)
            {
                Queue<int> draws = new([first, second]);
                var pattern = LetterHiding.Create("abcd", _ => draws.Dequeue());
                var key = string.Join(",", pattern.Select((value, index) => (value, index)).Where(x => x.value is null).Select(x => x.index));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        Assert.Equal(3, counts.Count);
        Assert.All(counts.Values, count => Assert.Equal(2, count));
    }
}
