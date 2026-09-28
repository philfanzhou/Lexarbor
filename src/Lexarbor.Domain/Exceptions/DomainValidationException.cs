namespace Lexarbor.Domain.Exceptions;

/// <summary>
/// A request the domain rules reject. Subclasses may carry structured detail
/// a specific caller knows how to report; the generic translation is still a
/// 400 with the message.
/// </summary>
public class DomainValidationException : Exception
{
    public DomainValidationException(string message)
        : base(message)
    {
    }

    public DomainValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
