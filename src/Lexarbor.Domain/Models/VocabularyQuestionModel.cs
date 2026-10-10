namespace Lexarbor.Domain.Models;

public sealed class VocabularyQuestionModel
{
    public string Word { get; init; } = string.Empty;

    /// <summary>The id of the word the question is about.</summary>
    public string WordId { get; init; } = string.Empty;

    /// <summary>
    /// The id of the meaning the question is about — the one drawn at random
    /// when the request did not name one.
    /// </summary>
    public string MeaningId { get; init; } = string.Empty;

    /// <summary>The direction this question was generated for.</summary>
    public bool ChineseToEnglish { get; init; }

    public IReadOnlyList<VocabularyQuestionOptionModel> Options { get; init; }
        = Array.Empty<VocabularyQuestionOptionModel>();
}
