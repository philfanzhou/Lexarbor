namespace Lexarbor.Domain.Exceptions;

/// <summary>
/// The write could not be applied because another connection holds the
/// database. Unlike <see cref="ConflictException"/> this says nothing about the
/// data: the request was well formed and the same request usually succeeds when
/// it is retried, so it is reported as a temporary condition rather than as a
/// conflict or an unexpected failure.
/// </summary>
public sealed class StorageBusyException : Exception
{
    public StorageBusyException(string message)
        : base(message)
    {
    }

    public StorageBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
