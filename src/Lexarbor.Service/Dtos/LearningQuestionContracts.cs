using System.Text.Json.Serialization;
using Lexarbor.Domain.Models;

namespace Lexarbor.Service.Dtos;

public sealed class LearningQuestionRequest
{
    public string? Mode { get; set; }
    public string? WordId { get; set; }
    public string? BookId { get; set; }
    public string? MeaningId { get; set; }
    public string? UnitId { get; set; }
    public bool? SameEntryKind { get; set; }
}

public sealed class SubmissionLearningQuestionRequest
{
    public string? Mode { get; set; }
}

public sealed record LearningQuestionOptionDto(string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool IsCorrect);

public sealed record ChoiceLearningQuestionDto(string Type, string Prompt, IReadOnlyList<LearningQuestionOptionDto> Options);
public sealed record FillLettersLearningQuestionDto(string Type, string Prompt, IReadOnlyList<string?> Pattern, string AnswerWord);

public static class LearningQuestionDto
{
    public static object From(LearningQuestion question) => question switch
    {
        ChoiceLearningQuestion choice => new ChoiceLearningQuestionDto(choice.Type.ToString(), choice.Prompt,
            choice.Options.Select(option => new LearningQuestionOptionDto(option.Text, option.IsCorrect)).ToList()),
        FillLettersLearningQuestion fill => new FillLettersLearningQuestionDto(fill.Type.ToString(), fill.Prompt, fill.Pattern, fill.AnswerWord),
        _ => throw new ArgumentOutOfRangeException(nameof(question))
    };
}
