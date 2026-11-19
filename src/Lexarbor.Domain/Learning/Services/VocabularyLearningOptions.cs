namespace Lexarbor.Learning.Domain.Services;
public sealed class VocabularyLearningOptions
{
    public const string SectionName = "VocabularyLearning";

    public int SlowAnswerMs { get; set; } = 8000;

    /// <summary>Correct fill-in-letters answers above this duration receive Hard.</summary>
    public int FillLettersSlowAnswerMs { get; set; } = 15000;

    public int QuestionExpiryMinutes { get; set; } = 30;

    public static IEnumerable<string> Validate(VocabularyLearningOptions options)
    {
        if (options.SlowAnswerMs <= 0)
        {
            yield return $"{SectionName}:SlowAnswerMs must be greater than 0.";
        }

        if (options.FillLettersSlowAnswerMs <= 0)
        {
            yield return $"{SectionName}:FillLettersSlowAnswerMs must be greater than 0.";
        }

        if (options.QuestionExpiryMinutes < 1)
        {
            yield return $"{SectionName}:QuestionExpiryMinutes must be at least 1.";
        }
    }
}
