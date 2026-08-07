namespace Lexarbor.Host.Authentication;

/// <summary>
/// Selected by <c>AdminAuthentication:Provider</c>. One implementation is registered at
/// startup; there is no runtime switching.
/// </summary>
public enum AdminAuthenticationProvider
{
    /// <summary>
    /// Standard OAuth2 resource owner password credentials against an OIDC provider.
    /// </summary>
    Oidc = 0,

    /// <summary>
    /// Optional gateway-style JSON token contract with application credentials in headers.
    /// </summary>
    Gateway = 1
}

public static class AdminAuthenticationHttpClient
{
    /// <summary>
    /// Named client shared by every provider so tests and policies have a single
    /// injection point.
    /// </summary>
    public const string Name = "LexarborIdentity";
}
