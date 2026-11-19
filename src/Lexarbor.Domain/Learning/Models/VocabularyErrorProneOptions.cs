namespace Lexarbor.Learning.Domain.Models;

public sealed class VocabularyErrorProneOptions
{
    public const string SectionName = "VocabularyLearning:ErrorProne";
    public int WindowDays { get; set; } = 30;
    public int MinimumAttempts { get; set; } = 3;
    public double ErrorRateThreshold { get; set; } = 0.4;
    public int MinimumForgetCount { get; set; } = 2;
}
