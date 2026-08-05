namespace Ruoyu.Study.Vocabulary.Domain.Exceptions;

public sealed class ResourceNotFoundException : Exception
{
    public ResourceNotFoundException(string message)
        : base(message)
    {
    }

    public ResourceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
