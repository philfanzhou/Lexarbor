namespace Ruoyu.Study.Vocabulary.Domain.Models;

public sealed class VocabularyQuestionModel
{
    public string Word { get; init; } = string.Empty;
    public IReadOnlyList<VocabularyQuestionOptionModel> Options { get; init; }
        = Array.Empty<VocabularyQuestionOptionModel>();
}
