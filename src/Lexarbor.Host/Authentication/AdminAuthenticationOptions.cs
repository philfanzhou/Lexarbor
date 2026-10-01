namespace Lexarbor.Host.Authentication;

public sealed class AdminAuthenticationOptions
{
    public const string SectionName = "AdminAuthentication";

    /// <summary>
    /// Retained only as a compatibility tripwire. The password proxy providers have
    /// been removed; hosted (authorization code + PKCE) login is the only
    /// administrator sign-in. The key must be unset or exactly <c>OidcCode</c> — any
    /// other value fails startup so an operator cannot believe password login still
    /// exists. It selects nothing.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Role required by the <c>VocabularyAdmin</c> policy and by hosted sign-in.
    /// </summary>
    public string RequiredRole { get; set; } = "admin";
}
