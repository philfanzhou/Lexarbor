namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabPlanNotFoundException : Exception
{
    public VocabPlanNotFoundException(Guid planId) : base($"Vocabulary plan {planId} was not found.") { }

    private VocabPlanNotFoundException(string message) : base(message) { }

    public static VocabPlanNotFoundException NoActivePlan() => new("No active vocabulary plan exists for this student.");
}

public sealed class VocabPlanActiveExistsException : Exception
{
    public VocabPlanActiveExistsException() : base("An active vocabulary plan already exists for this student.") { }
}

public sealed class VocabPlanVersionConflictException : Exception
{
    public VocabPlanVersionConflictException() : base("The vocabulary plan was modified concurrently; reload and retry.") { }
}

public sealed class VocabPlanForbiddenException : Exception
{
    public VocabPlanForbiddenException(string message) : base(message) { }
}

public sealed class VocabPlanNotActiveException : Exception
{
    public VocabPlanNotActiveException() : base("The vocabulary plan is no longer active.") { }
}

public sealed class VocabPlanScopeEmptyException : Exception
{
    public VocabPlanScopeEmptyException() : base("The vocabulary plan scope contains no words.") { }
}

public sealed class VocabPlanScopeInvalidException : Exception
{
    public VocabPlanScopeInvalidException(string message) : base(message) { }
}
