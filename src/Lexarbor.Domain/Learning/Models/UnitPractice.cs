using System.Text;

namespace Lexarbor.Learning.Domain.Models;

public sealed record UnitPracticeTarget(string WordId, string MeaningId, string Word, string Meaning, string? PartOfSpeech);
public sealed record UnitPracticeQuestion(Guid QuestionId, string Meaning, string? PartOfSpeech, string? SpellingHint, int Position);
public sealed record UnitPracticeFeedback(Guid QuestionId, bool Correct, UnitPracticeTarget Target);
public sealed record UnitPracticeMistake(UnitPracticeTarget Target, bool Corrected);
public sealed record UnitPracticeProgress(Guid SessionId, string BookId, string UnitId, int Round,
    int Total, int Answered, int FirstPassCorrect, int RoundTotal, int RoundAnswered,
    UnitPracticeQuestion? NextQuestion, IReadOnlyList<UnitPracticeMistake> Mistakes);


// Two different spellings can share the same definition and part of speech in a unit.
// Reveal only positions needed to distinguish those targets during a spelling exercise.
public static class UnitPracticeSpelling
{
    public static IReadOnlyDictionary<string, string> Hints(IReadOnlyList<UnitPracticeTarget> targets)
    {
        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in targets.GroupBy(item => (item.Meaning, item.PartOfSpeech)).Where(g => g.Count() > 1))
        {
            var spellings = group.Select(item => (Target: item, Letters: item.Word.EnumerateRunes().ToArray())).ToArray();
            foreach (var (target, letters) in spellings)
            {
                var others = spellings.Where(s => s.Letters.Length == letters.Length
                    && !string.Equals(s.Target.Word, target.Word, StringComparison.OrdinalIgnoreCase)).ToList();
                var hint = Enumerable.Repeat("_", letters.Length).ToArray();
                for (var i = 0; i < letters.Length && others.Count > 0; i++)
                {
                    if (others.RemoveAll(s => Rune.ToUpperInvariant(s.Letters[i]) != Rune.ToUpperInvariant(letters[i])) > 0)
                        hint[i] = letters[i].ToString();
                }
                hints[target.MeaningId] = string.Concat(hint);
            }
        }
        return hints;
    }
}
