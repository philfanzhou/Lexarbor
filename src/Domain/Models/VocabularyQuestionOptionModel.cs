namespace Lexarbor.Domain.Models;

public sealed class VocabularyQuestionOptionModel
{
    public string Text { get; init; } = string.Empty;
    public bool IsCorrect { get; init; }
}
