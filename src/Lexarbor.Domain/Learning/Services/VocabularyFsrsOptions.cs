namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyFsrsOptions
{
    public const string SectionName = "VocabularyLearning:Fsrs";

    public double DesiredRetention { get; set; } = 0.9;

    public double FamiliarThresholdDays { get; set; } = 7.0;

    public double MasteredThresholdDays { get; set; } = 21.0;

    public int[] LearningStepsMinutes { get; set; } = [1, 10];

    public int[] RelearningStepsMinutes { get; set; } = [10];

    public int MaximumIntervalDays { get; set; } = 36500;
    public static IEnumerable<string> Validate(VocabularyFsrsOptions options)
    {
        if (options.DesiredRetention <= 0 || options.DesiredRetention >= 1 ||
            double.IsNaN(options.DesiredRetention))
        {
            yield return $"{SectionName}:DesiredRetention must be greater than 0 and less than 1.";
        }

        if (double.IsNaN(options.FamiliarThresholdDays) || options.FamiliarThresholdDays <= 0)
        {
            yield return $"{SectionName}:FamiliarThresholdDays must be greater than 0.";
        }

        if (double.IsNaN(options.MasteredThresholdDays) ||
            options.MasteredThresholdDays <= options.FamiliarThresholdDays)
        {
            yield return $"{SectionName}:MasteredThresholdDays must be greater than FamiliarThresholdDays.";
        }

        foreach (var (name, steps) in new[]
                 {
                     ($"{SectionName}:LearningStepsMinutes", options.LearningStepsMinutes),
                     ($"{SectionName}:RelearningStepsMinutes", options.RelearningStepsMinutes),
                 })
        {
            if (steps is null)
            {
                yield return $"{name} must not be null.";
            }
            else if (steps.Any(step => step <= 0))
            {
                yield return $"{name} must only contain positive minute values.";
            }
        }

        if (options.MaximumIntervalDays < 1)
        {
            yield return $"{SectionName}:MaximumIntervalDays must be at least 1.";
        }
    }
}
