using System.Text;
using System.Text.Json.Serialization;

namespace Lexarbor.Learning.Domain.Models;

public sealed record UnitPracticeTarget(string WordId, string MeaningId, string Word, string Meaning, string? PartOfSpeech);

/// <summary>How a session asks its questions. The learner picks one when starting; retries keep it.</summary>
public enum UnitPracticeMode { Spelling, Choice, Mixed }

public static class UnitPracticeModes
{
    public static string Name(this UnitPracticeMode mode) => mode switch
    {
        UnitPracticeMode.Choice => "choice",
        UnitPracticeMode.Mixed => "mixed",
        _ => "spelling"
    };

    /// <summary>An omitted mode is spelling, the only mode before modes existed.</summary>
    public static bool TryParse(string? value, out UnitPracticeMode mode)
    {
        mode = value switch { "choice" => UnitPracticeMode.Choice, "mixed" => UnitPracticeMode.Mixed, _ => UnitPracticeMode.Spelling };
        return value is null or "spelling" or "choice" or "mixed";
    }
}

/// <summary>
/// A generated question kept with its practice item. Spelling questions are not stored: the item's
/// meaning, part of speech and hint describe them. Correct flags and the answer word stay server-side.
/// </summary>
public sealed record UnitPracticeIssued(string Type, string Prompt, IReadOnlyList<UnitPracticeChoice>? Options = null,
    IReadOnlyList<string?>? Pattern = null, string? AnswerWord = null, string? AnchorWord = null, string? PartOfSpeech = null)
{
    public const string Spelling = "Spelling";

    /// <summary>The same question with fresh option IDs in a new order, used when a retry cannot be regenerated.</summary>
    public UnitPracticeIssued Reissue(Func<int, int> pick)
    {
        if (Options == null) return this;
        var options = Options.Select(option => option with { OptionId = Guid.NewGuid() }).ToList();
        for (var i = options.Count - 1; i > 0; i--)
        {
            var j = pick(i + 1);
            (options[i], options[j]) = (options[j], options[i]);
        }
        return this with { Options = options };
    }
}

public sealed record UnitPracticeChoice(Guid OptionId, string Text, bool IsCorrect);
public sealed record UnitPracticeOption(Guid OptionId, string Text);

/// <summary>
/// The issued question as the learner sees it. Type is Spelling or a learning-question type name.
/// Spelling uses Meaning, PartOfSpeech and SpellingHint; other types use Prompt plus their own fields.
/// </summary>
public sealed record UnitPracticeQuestion(Guid QuestionId, int Position, string Type, string? Meaning, string? PartOfSpeech,
    string? SpellingHint, string? Prompt = null, IReadOnlyList<UnitPracticeOption>? Options = null,
    IReadOnlyList<string?>? Pattern = null, string? AnchorWord = null);

/// <summary>An answer: exactly one of a full spelling, an option, hidden letters or unknown.</summary>
public sealed record UnitPracticeAnswer(string? Text, Guid? OptionId, IReadOnlyList<string>? Letters, bool Unknown);

public sealed record UnitPracticeFeedback(Guid QuestionId, bool Correct, UnitPracticeTarget Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? CorrectOptionId);
public sealed record UnitPracticeMistake(UnitPracticeTarget Target, bool Corrected);
public sealed record UnitPracticeProgress(Guid SessionId, string BookId, string UnitId, UnitPracticeMode Mode, int Round,
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
                var revealed = 0;
                for (var i = 0; i < letters.Length && others.Count > 0; i++)
                {
                    // If every position is needed, describe the remaining alternatives at
                    // the last position. Showing that letter would give away the full answer.
                    if (i == letters.Length - 1 && revealed == i)
                    {
                        var excluded = others.Select(s => Rune.ToUpperInvariant(s.Letters[i])).Distinct();
                        hint[i] = $"[≠{string.Join("/", excluded)}]";
                        break;
                    }
                    if (others.RemoveAll(s => Rune.ToUpperInvariant(s.Letters[i]) != Rune.ToUpperInvariant(letters[i])) > 0)
                    {
                        hint[i] = letters[i].ToString();
                        revealed++;
                    }
                }
                hints[target.MeaningId] = string.Concat(hint);
            }
        }
        return hints;
    }
}

public static class UnitPracticeGrading
{
    /// <summary>
    /// Grades an answer against the issued question. Returns null when the answer kind does not suit the
    /// question type: Spelling takes text, choices take one of their option IDs, FillLetters takes one
    /// letter per blank. Unknown suits every type and is wrong.
    /// </summary>
    public static bool? Judge(UnitPracticeIssued? issued, string word, UnitPracticeAnswer answer)
    {
        var given = (answer.Text != null ? 1 : 0) + (answer.OptionId != null ? 1 : 0) + (answer.Letters != null ? 1 : 0) + (answer.Unknown ? 1 : 0);
        if (given != 1) return null;
        if (issued == null)
            return answer.Unknown ? false : answer.Text != null && !string.IsNullOrWhiteSpace(answer.Text)
                ? string.Equals(answer.Text.Trim(), word.Trim(), StringComparison.OrdinalIgnoreCase) : null;
        if (issued.Options != null)
        {
            if (answer.Unknown) return false;
            var option = issued.Options.FirstOrDefault(item => item.OptionId == answer.OptionId);
            return option?.IsCorrect;
        }
        if (issued.Pattern == null || issued.AnswerWord == null) return null;
        if (answer.Unknown) return false;
        return answer.Letters == null ? null : JudgeLetters(issued.Pattern, issued.AnswerWord, answer.Letters);
    }

    private static bool? JudgeLetters(IReadOnlyList<string?> pattern, string answerWord, IReadOnlyList<string> letters)
    {
        var answer = answerWord.EnumerateRunes().ToArray();
        if (pattern.Count != answer.Length || letters.Count != pattern.Count(value => value is null)) return null;
        var correct = true;
        var index = 0;
        for (var position = 0; position < pattern.Count; position++)
        {
            if (pattern[position] is not null) continue;
            var value = letters[index++];
            if (string.IsNullOrEmpty(value) || !Rune.TryGetRuneAt(value, 0, out var submitted)
                || submitted.Utf16SequenceLength != value.Length || !Rune.IsLetter(submitted))
                return null;
            correct &= Rune.ToUpperInvariant(submitted) == Rune.ToUpperInvariant(answer[position]);
        }
        return correct;
    }
}
