namespace Lexarbor.Host.Authentication;

public static class AdminSessionCookie
{
    /// <summary>
    /// The opaque hosted-login session cookie of the HTTPS profile. The name is
    /// byte-for-byte the one the retired in-house implementation issued, so existing
    /// rows and protected payloads keep authenticating across the migration (and
    /// across a rollback). It is applied to the cookie session scheme at
    /// registration when the redirect URI is not plain HTTP.
    /// </summary>
    public const string Name = "__Host-Lexarbor.AdminSession";

    /// <summary>
    /// The session cookie of the plain-HTTP profile: the same name without the
    /// <c>__Host-</c> prefix. The cookie profile derives from the redirect URI's
    /// scheme: an <c>http</c> redirect rides the plain-HTTP profile and drops the
    /// <c>__Host-</c> prefix — the prefix demands the Secure attribute, which a
    /// plain-HTTP deployment cannot set.
    /// </summary>
    public const string PlainHttpName = "Lexarbor.AdminSession";

    /// <summary>
    /// The session cookie name for a configured redirect URI, by the same rule the
    /// cookie session scheme registration applies: an absolute plain-<c>http</c>
    /// redirect URI rides the plain-HTTP profile and drops the <c>__Host-</c> prefix;
    /// everything else — <c>https</c>, or a blank redirect URI in the optional-login
    /// mode — keeps the historical byte-for-byte name.
    /// </summary>
    public static string ForRedirectUri(string? redirectUri) =>
        IsPlainHttpRedirect(redirectUri) ? PlainHttpName : Name;

    /// <summary>
    /// Whether the plain-HTTP cookie profile is active for this redirect URI — the
    /// same input the cookie scheme registration reads (an absolute <c>http</c>
    /// redirect URI; illegal values fail startup validation and never reach a request).
    /// </summary>
    internal static bool IsPlainHttpRedirect(string? redirectUri) =>
        Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp;

    /// <summary>
    /// Deletes the session cookie the active profile actually issues — derived from the
    /// configured redirect URI by the same rule the cookie scheme registration applies — with
    /// the attributes of that profile: a plain-HTTP deletion must not carry Secure, or a real
    /// browser would refuse the header and keep the cookie.
    /// </summary>
    public static void Clear(HttpContext context)
    {
        var code = context.RequestServices.GetRequiredService<
            Microsoft.Extensions.Options.IOptionsMonitor<OidcCodeOptions>>()
            .CurrentValue;
        context.Response.Cookies.Delete(ForRedirectUri(code.RedirectUri), new CookieOptions
        {
            HttpOnly = true,
            Secure = !IsPlainHttpRedirect(code.RedirectUri),
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
    }
}
