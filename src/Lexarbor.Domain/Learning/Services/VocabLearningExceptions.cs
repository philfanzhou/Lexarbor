namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabAudioLockedException : Exception
{
    public VocabAudioLockedException() : base("Pronunciation is available after answering the question.") { }
}

public sealed class VocabAudioUnavailableException : Exception
{
    public VocabAudioUnavailableException() : base("Pronunciation is temporarily unavailable.") { }
}

public sealed class VocabQuestionNotFoundException : Exception
{
    public VocabQuestionNotFoundException(Guid questionId) : base($"Vocabulary question {questionId} was not found.") { }
}

public sealed class VocabQuestionExpiredException : Exception
{
    public VocabQuestionExpiredException() : base("The vocabulary question has expired and can no longer be answered.") { }
}

public sealed class VocabOptionInvalidException : Exception
{
    public VocabOptionInvalidException() : base("The selected option does not belong to the question.") { }
}

public sealed class VocabDayNotCompletedException : Exception
{
    public VocabDayNotCompletedException() : base("The daily check-in must be completed before learning another group.") { }
}
public sealed class VocabCatalogUnavailableException : Exception
{
    public VocabCatalogUnavailableException() : base("The vocabulary catalog is temporarily unavailable.") { }
}

public sealed class VocabAttemptDuplicateException : Exception
{
    public VocabAttemptDuplicateException() : base("The question has already been answered concurrently.") { }
}

public sealed class VocabPlanDayConflictException : Exception
{
    public VocabPlanDayConflictException() : base("The vocabulary plan day was modified concurrently; reload and retry.") { }
}
