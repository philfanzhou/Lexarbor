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
    /// official SignaCore client accepts an explicit loopback HTTP origin
    /// (<c>127.0.0.1</c> / <c>[::1]</c>) in the Development and Testing environments only,
    /// and every cookie it issues carries <c>Secure</c>. Any value left here fails startup
    /// so an operator cannot believe the private-network plain-HTTP mode still exists. See
    /// <c>docs/development/HostedLoginReleaseNotes.md</c> and Deployment.md.
    /// </summary>
    public string? HttpTestOrigins { get; set; }

    internal const string RemovedSettingFailureMessage =
        "AdminAuthentication:HttpTestOrigins no longer exists. The Testing-only plain-HTTP " +
        "transport for the hosted administrator login was removed together with the in-house " +
        "implementation; the official SignaCore client accepts only an explicit loopback HTTP " +
        "origin (127.0.0.1 or [::1]) in the Development and Testing environments and always " +
        "issues Secure cookies. Remove the setting and serve test deployments over a loopback " +
        "or HTTPS origin as documented in docs/development/Deployment.md.";
}
