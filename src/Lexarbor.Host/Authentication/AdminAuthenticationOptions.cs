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
    /// Role required for a principal to be mapped onto the ServiceMantle
    /// management identity, and by hosted sign-in.
    /// </summary>
    public string RequiredRole { get; set; } = "admin";

    /// <summary>
    /// Retained only as a compatibility tripwire. The Testing-only plain-HTTP transport
    /// for hosted administrator login was removed with the in-house implementation: the
    /// official SignaCore client accepts <c>http</c> and <c>https</c> redirect URIs
    /// alike in every environment and derives every cookie's name suffix and Secure
    /// attribute from the redirect URI's scheme, so no allowlist or other replacement
    /// configuration is needed. Any value left here fails startup so an operator cannot
    /// believe the removed opt-in mode still exists. See
    /// <c>docs/development/HostedLoginReleaseNotes.md</c> and Deployment.md.
    /// </summary>
    public string? HttpTestOrigins { get; set; }

    internal const string RemovedSettingFailureMessage =
        "AdminAuthentication:HttpTestOrigins no longer exists. The Testing-only plain-HTTP " +
        "transport for the hosted administrator login was removed together with the in-house " +
        "implementation; the official SignaCore client accepts http and https redirect URIs " +
        "alike in every environment and derives the cookie names and attributes from the " +
        "redirect URI's scheme, so no replacement configuration is needed. Remove the " +
        "setting; see docs/development/Deployment.md.";
}
