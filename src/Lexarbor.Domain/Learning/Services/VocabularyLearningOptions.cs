using Lexarbor.Learning.Domain.Calendar;

namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyLearningOptions
{
    public const string SectionName = "VocabularyLearning";

    public int SlowAnswerMs { get; set; } = 8000;

    /// <summary>Correct fill-in-letters answers above this duration receive Hard.</summary>
    public int FillLettersSlowAnswerMs { get; set; } = 15000;

    public int QuestionExpiryMinutes { get; set; } = 30;

    /// <summary>
    /// The time zone ID that splits learning days, such as <c>Asia/Shanghai</c>. Unset means
    /// UTC+08:00 without daylight saving time.
    /// </summary>
    public string? TimeZone { get; set; }

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

        if (!string.IsNullOrWhiteSpace(options.TimeZone) && !IsKnownTimeZone(options.TimeZone))
        {
            yield return $"{SectionName}:TimeZone must be a time zone ID known to the host, such as Asia/Shanghai.";
        }
    }

    private static bool IsKnownTimeZone(string id)
    {
        try
        {
            LearningCalendar.Create(id);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
