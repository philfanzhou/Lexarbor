namespace Lexarbor.Domain.Services;

/// <summary>The deployment names no supplementary book, so submissions are off.</summary>
public sealed class SubmissionsNotConfiguredException : Exception
{
    public SubmissionsNotConfiguredException()
        : base("Meaning submissions are not configured.")
    {
    }
}
