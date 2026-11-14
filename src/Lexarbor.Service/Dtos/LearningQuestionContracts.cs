using System.Text.Json;
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
    public JsonElement Relation { get; set; }
}

public sealed class SubmissionLearningQuestionRequest
{
    public string? Mode { get; set; }
}

public sealed record LearningQuestionOptionDto(string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool IsCorrect);

public sealed record ChoiceLearningQuestionDto(string Type, string Prompt, IReadOnlyList<LearningQuestionOptionDto> Options);
public sealed record FillLettersLearningQuestionDto(string Type, string Prompt, IReadOnlyList<string?> Pattern, string AnswerWord);
public sealed record FormChoiceLearningQuestionDto(string Type, string Prompt, string RelationType, IReadOnlyList<LearningQuestionOptionDto> Options);
public sealed record RelationChoiceLearningQuestionDto(string Type, string Prompt, string AnchorWord, IReadOnlyList<LearningQuestionOptionDto> Options);
public sealed record FormFillLearningQuestionDto(string Type, string Prompt, string AnchorWord, string RelationType, IReadOnlyList<string?> Pattern, string AnswerWord);
public sealed record PartOfSpeechChoiceLearningQuestionDto(string Type, string Prompt, string AnchorWord, string PartOfSpeech, IReadOnlyList<LearningQuestionOptionDto> Options);

public static class LearningQuestionDto
{
    public static object From(LearningQuestion question) => question switch
    {
        ChoiceLearningQuestion choice => new ChoiceLearningQuestionDto(choice.Type.ToString(), choice.Prompt, Options(choice.Options)),
        FillLettersLearningQuestion fill => new FillLettersLearningQuestionDto(fill.Type.ToString(), fill.Prompt, fill.Pattern, fill.AnswerWord),
        FormChoiceLearningQuestion form => new FormChoiceLearningQuestionDto(form.Type.ToString(), form.Prompt, form.RelationType, Options(form.Options)),
        RelationChoiceLearningQuestion relation => new RelationChoiceLearningQuestionDto(relation.Type.ToString(), relation.Prompt, relation.AnchorWord, Options(relation.Options)),
        FormFillLearningQuestion fill => new FormFillLearningQuestionDto(fill.Type.ToString(), fill.Prompt, fill.AnchorWord, fill.RelationType, fill.Pattern, fill.AnswerWord),
        PartOfSpeechChoiceLearningQuestion choice => new PartOfSpeechChoiceLearningQuestionDto(choice.Type.ToString(), choice.Prompt, choice.AnchorWord, choice.PartOfSpeech, Options(choice.Options)),
        _ => throw new ArgumentOutOfRangeException(nameof(question))
    };

    private static LearningQuestionOptionDto[] Options(IReadOnlyList<VocabularyQuestionOptionModel> options) =>
        options.Select(option => new LearningQuestionOptionDto(option.Text, option.IsCorrect)).ToArray();
}
