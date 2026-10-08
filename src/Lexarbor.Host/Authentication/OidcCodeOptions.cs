namespace Lexarbor.Host.Authentication;

public sealed class OidcCodeOptions
{
    public const string SectionName = "AdminAuthentication:OidcCode";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string Scope { get; set; } = "openid profile";

    /// <summary>
    /// Optional post-logout redirect URI for the prepared logout, sent byte-for-byte
    /// and matched exactly against the provider registration. It must target this
    /// service's fixed <c>/admin/auth/logout/return</c> route; when empty or invalid,
    /// prepared logout proceeds without a return redirect.
    /// </summary>
    public string PostLogoutRedirectUri { get; set; } = string.Empty;
}
