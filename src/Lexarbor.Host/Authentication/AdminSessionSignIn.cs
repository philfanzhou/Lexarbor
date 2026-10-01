using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// Trusted Host callers only: validate token signature, issuer, audience and the
/// token-to-principal association before calling. Future callbacks must also
/// complete their OIDC checks. This is not a browser input or token validator.
/// </summary>
public interface IAdminSessionSignIn
{
    Task SignInAsync(HttpContext context, ClaimsPrincipal validatedPrincipal, string accessToken,
        string? idToken = null, CancellationToken cancellationToken = default);
}

public sealed class AdminSessionSignIn(AdminSessionStore store, TimeProvider clock,
    IOptionsMonitor<AdminAuthenticationOptions> options) : IAdminSessionSignIn
{    public async Task SignInAsync(HttpContext context, ClaimsPrincipal validatedPrincipal, string accessToken,
        string? idToken = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issuer = validatedPrincipal.FindFirst("iss")?.Value;
        var subject = validatedPrincipal.FindFirst("sub")?.Value
            ?? validatedPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (validatedPrincipal.Identity?.IsAuthenticated != true
            || !VocabularyClaims.HasRole(validatedPrincipal, options.CurrentValue.RequiredRole)
            || string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)
            || !long.TryParse(validatedPrincipal.FindFirst("exp")?.Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var expirySeconds)
            || expirySeconds < DateTimeOffset.MinValue.ToUnixTimeSeconds()
            || expirySeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            throw new ArgumentException("An independently validated administrator principal with issuer, subject and expiry is required.", nameof(validatedPrincipal));
        var expiry = DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
        var session = new ValidatedAdminSession
        {
            AccessToken = accessToken,
            IdToken = idToken,
            Issuer = issuer,
            Subject = subject,
            DisplayName = VocabularyClaims.GetDisplayName(validatedPrincipal),
            Roles = VocabularyClaims.GetRoles(validatedPrincipal).ToArray(),
            AccessTokenExpiresAt = expiry
        };
        context.Request.Cookies.TryGetValue(AdminSessionCookie.Name, out var oldHandle);
        var handle = await store.ReplaceAsync(oldHandle, session, cancellationToken);
        // Only a confirmed commit reaches here. A lost commit/response is unknown:
        // no compensation, automatic retry, or promise that the old row survived.
        var cookie = AdminSessionCookie.Options();
        cookie.Expires = expiry;
        cookie.MaxAge = TimeSpan.FromSeconds(Math.Max(0, Math.Floor((expiry - clock.GetUtcNow()).TotalSeconds)));
        context.Response.Cookies.Append(AdminSessionCookie.Name, handle, cookie);
        // Sign-in replaces every credential the browser may hold from a previous
        // session, including the retired password-login JWT cookie.
        context.Response.Cookies.Delete(AdminSessionCookie.LegacyJwtName, AdminSessionCookie.LegacyOptions());
    }
}
