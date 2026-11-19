namespace Lexarbor.Learning.Domain.Services;

public sealed class MemoryCardConflictException : Exception
{
    public MemoryCardConflictException()
        : base("The memory card was modified concurrently; retry the review.")
    {
    }
}
