namespace Lexarbor.Host.Integration;

public sealed class AssistantUnavailableException : Exception
{
    public AssistantUnavailableException()
        : base("The review assistant did not return usable advice.")
    {
    }
}
