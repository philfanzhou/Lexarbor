using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The resolved protocol settings of the admin hosted-login slice, read from the historical
/// configuration keys (<see cref="IdentityServiceOptions"/> authority plus the
/// <see cref="OidcCodeOptions"/> client credentials) so the container environment contract is
/// unchanged. The load runs inside the options pipeline — not at service registration — so a
/// test host's late configuration is seen exactly like a deployment's composed configuration.
/// A deployment that leaves any of the three client keys blank keeps the unconfigured
/// degradation — every hosted endpoint answers its fixed degraded shape and the public API
/// keeps serving — while a configured but illegal value fails startup instead of silently
/// downgrading, exactly like the replaced SignaCore client's missing-versus-illegal split.
/// </summary>
public sealed class AdminOidcSettings
{
    public const string CallbackPath = "/admin/auth/callback";
    public const string LogoutReturnPath = "/admin/auth/logout/return";

    public bool Available { get; internal set; }
    public string Authority { get; internal set; } = string.Empty;
    public string ClientId { get; internal set; } = string.Empty;
    public string ClientSecret { get; internal set; } = string.Empty;
    public string RedirectUri { get; internal set; } = string.Empty;
    public string Scope { get; internal set; } = "openid profile";
    /// <summary>The registered post-logout URI, or null when none is configured: logout then
    /// proceeds without a return redirect and the fixed return route cannot complete.</summary>
    public string? PostLogoutRedirectUri { get; internal set; }

    /// <summary>The deployment's explicit metadata-transport statement
    /// (<see cref="IdentityServiceOptions.RequireHttpsMetadata"/>), or null for the
    /// environment-and-loopback default the Bearer trust shares.</summary>
    public bool? RequireHttpsMetadata { get; internal set; }

    /// <summary>Whether the plain-HTTP cookie profile is active: an absolute <c>http</c>
    /// redirect URI selects the deployment's no-<c>Secure</c>, de-prefixed cookie set, exactly
    /// as the replaced client derived its whole cookie profile from the redirect URI's
    /// scheme.</summary>
    public bool PlainHttpProfile => AdminSessionCookie.IsPlainHttpRedirect(RedirectUri);

    /// <summary>
    /// Loads and validates the settings from the composed configuration. A blank client key
    /// keeps the unconfigured mode; an illegal configured value throws
    /// <see cref="OptionsValidationException"/> so startup fails naming the setting — never
    /// its value.
    /// </summary>
    public void Load(IConfiguration configuration)
    {
        var identity = configuration.GetSection(IdentityServiceOptions.SectionName).Get<IdentityServiceOptions>()
            ?? new IdentityServiceOptions();
        var code = configuration.GetSection(OidcCodeOptions.SectionName).Get<OidcCodeOptions>()
            ?? new OidcCodeOptions();
        var clientConfigured = !string.IsNullOrWhiteSpace(code.ClientId)
            || !string.IsNullOrWhiteSpace(code.ClientSecret)
            || !string.IsNullOrWhiteSpace(code.RedirectUri);
        if (!clientConfigured)
        {
            // The historical placeholder authority stays out of the protocol options in the
            // unconfigured mode, so nothing ever dials it.
            Available = false;
            return;
        }

        Available = !string.IsNullOrWhiteSpace(code.ClientId)
            && !string.IsNullOrWhiteSpace(code.ClientSecret)
            && !string.IsNullOrWhiteSpace(code.RedirectUri);
        Authority = identity.Authority;
        ClientId = code.ClientId;
        ClientSecret = code.ClientSecret;
        RedirectUri = code.RedirectUri;
        Scope = string.IsNullOrWhiteSpace(code.Scope) ? "openid profile" : code.Scope;
        PostLogoutRedirectUri = string.IsNullOrWhiteSpace(code.PostLogoutRedirectUri)
            ? null
            : code.PostLogoutRedirectUri;
        RequireHttpsMetadata = identity.RequireHttpsMetadata;

        // A blank client key keeps the unconfigured degradation (the replaced client treated
        // each blank value as missing, never as an illegal one); only configured values are
        // validated, so half-configuration degrades instead of failing startup.
        var failures = new List<string>();

        if (!IsAbsoluteHttpUri(Authority, allowPath: false, out var authorityFailure))
        {
            failures.Add("IdentityService:Authority " + authorityFailure);
        }

        if (!string.IsNullOrWhiteSpace(RedirectUri)
            && (!IsAbsoluteHttpUri(RedirectUri, allowPath: true, out var redirectFailure)
                || !string.Equals(Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect) ? redirect.AbsolutePath : string.Empty, CallbackPath, StringComparison.Ordinal)))
        {
            failures.Add("AdminAuthentication:OidcCode:RedirectUri must be an absolute http or https URI without query or fragment whose path is exactly " + CallbackPath + ".");
        }

        if (PostLogoutRedirectUri is { } postLogout
            && (!IsAbsoluteHttpUri(postLogout, allowPath: true, out var postLogoutFailure)
                || !string.Equals(Uri.TryCreate(postLogout, UriKind.Absolute, out var configured) ? configured.AbsolutePath : string.Empty, LogoutReturnPath, StringComparison.Ordinal)))
        {
            failures.Add("AdminAuthentication:OidcCode:PostLogoutRedirectUri must be an absolute http or https URI without query or fragment whose path is exactly " + LogoutReturnPath + ".");
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                "AdminOidcSettings", typeof(AdminOidcSettings), failures);
        }
    }

    private static bool IsAbsoluteHttpUri(string value, bool allowPath, out string failure)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failure = "must be an absolute http or https URI.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)
            || (!allowPath && !string.IsNullOrEmpty(uri.AbsolutePath.TrimEnd('/')))
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            failure = "must carry no user info, path, query or fragment.";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}
