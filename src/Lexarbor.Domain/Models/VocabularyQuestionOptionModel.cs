namespace Lexarbor.Domain.Models;

public sealed class VocabularyQuestionOptionModel
{
    public string Text { get; init; } = string.Empty;
    public bool IsCorrect { get; init; }

    /// <summary>
    /// The word this option was taken from. Both directions carry it: the
    /// options of a Chinese-to-English question are words, and each
    /// English-to-Chinese option is one meaning of one word.
    /// </summary>
    public string WordId { get; init; } = string.Empty;

    /// <summary>
    /// The meaning this option was taken from, or null when there is none to
    /// name: Chinese-to-English distractors are drawn as words, so no single
    /// meaning of such a word stands for the option.
    /// </summary>
    public string? MeaningId { get; init; }
}
