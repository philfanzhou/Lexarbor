using Lexarbor.Domain.Exceptions;

namespace Lexarbor.Domain.Services;

/// <summary>Text rules shared by the services that normalize administrator input.</summary>
internal static class DomainText
{
    /// <summary>The trimmed value, or a <see cref="DomainValidationException"/> carrying <paramref name="message"/> when it is blank.</summary>
    internal static string RequireNonBlank(string? value, string message)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new DomainValidationException(message);
        }

        return normalized;
    }
}
