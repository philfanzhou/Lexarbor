namespace Lexarbor.Domain.Exceptions;

public enum LearningQuestionFailure { SourceUnavailable, Unavailable }

/// <summary>Only the new learning-question routes translate these failures.</summary>
public sealed class LearningQuestionException(LearningQuestionFailure failure) : Exception
{
    public LearningQuestionFailure Failure { get; } = failure;
}
