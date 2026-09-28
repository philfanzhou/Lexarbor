namespace Lexarbor.Domain.Exceptions;

/// <summary>
/// A batch import in which one or more entries failed a check that needed the
/// database. Carries the per-entry failures so the caller can report each one
/// at its index; the middleware fallback answers it like any other validation
/// failure, with the message alone, when the caller does not catch it first.
/// </summary>
public sealed class BatchEntryValidationException : DomainValidationException
{
    public IReadOnlyList<(int Index, string Message)> EntryErrors { get; }

    public BatchEntryValidationException(
        string message,
        IReadOnlyList<(int Index, string Message)> entryErrors)
        : base(message)
    {
        EntryErrors = entryErrors;
    }
}
