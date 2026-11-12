using System.Globalization;
using System.Security.Claims;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The Lexarbor <see cref="ITicketStore"/> behind the framework cookie session handler's
/// <c>SessionStore</c> seam: the server-side session boundary backed by the existing SQLite
/// administrator session rows. The browser's opaque cookie references a 43-character handle of
/// the same canonical form the retired implementations issued, and the protected payload keeps
/// the byte-for-byte <c>v1</c> format, so the rows written before the handler swap (and before
/// a rollback) stay readable — only the cookie's own presentation changes (it now carries a
/// data-protected reference to the handle), so browsers re-authenticate once across the swap.
/// </summary>
/// <remarks>
/// <para>
/// Storage runs inside a fresh service scope per operation so the scoped
/// <c>AdminSessionRepository</c>/unit-of-work transaction semantics are preserved for a
/// singleton store. The sign-in audit row is staged as the storage write's participating write
/// and the logout audit row as the revocation's companion write, so each security event
/// commits — or rolls back — with exactly the session write it describes (A2).
/// </para>
/// <para>
/// The ID-token principal the handler hands over carries identity only; the administrator
/// roles are re-verified from the stored access token with the retained
/// <see cref="AdminAccessTokenValidator"/> before the ticket enters storage — once at the
/// pre-sign-in gate and again here, the store's own boundary — so the session's role claims
/// are never inferred from an unverified source (PS-12: the ID token carries no role).
/// </para>
/// </remarks>
public sealed class AdminSessionTicketStore(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<AdminAuthenticationOptions> adminOptions,
    ILogger<AdminSessionTicketStore> logger) : ITicketStore
{
    // The abstract context-less members are never exercised in this application: the cookie
    // handler — the only caller of the store — always passes the request's HttpContext (the
    // audit companion writes need it). They stay explicit so a context-less call fails with a
    // named error instead of a silent no-op.
    Task<string> ITicketStore.StoreAsync(AuthenticationTicket ticket) =>
        throw new NotSupportedException("The administrator session store requires the request's HttpContext.");

    Task ITicketStore.RenewAsync(string key, AuthenticationTicket ticket) =>
        throw new NotSupportedException("The administrator session store requires the request's HttpContext.");

    Task<AuthenticationTicket?> ITicketStore.RetrieveAsync(string key) =>
        throw new NotSupportedException("The administrator session store requires the request's HttpContext.");

    Task ITicketStore.RemoveAsync(string key) =>
        throw new NotSupportedException("The administrator session store requires the request's HttpContext.");

    public async Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return await ReplaceSessionAsync(null, ticket, context, cancellationToken);
    }

    /// <summary>
    /// Renews the session behind an already-presented handle. The framework cookie handler
    /// reuses the same key when a browser signs in again over a live session, so the row is
    /// replaced in place under the same handle (the old payload is revoked and the login audit
    /// row joins the same transaction) instead of rotating to a new one.
    /// </summary>
    public async Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await ReplaceSessionAsync(key, ticket, context, cancellationToken);
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AdminSessionStore>();
        var session = await store.ReadAsync(key, cancellationToken);
        return session is null ? null : ToTicket(session);
    }

    public async Task RemoveAsync(string key, HttpContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AdminSessionStore>();
        // The logout audit row joins the revocation's transaction: it is staged only for a
        // live revoked session, and commits or rolls back with that revocation. Concurrent or
        // repeated logouts of the same key find no live session and write nothing.
        var revoked = await store.RevokeAndReadAsync(
            key,
            scope.ServiceProvider.GetRequiredService<AdminAuthenticationAudit>().StageLogout(context),
            cancellationToken);
        if (revoked is not null)
        {
            // The live-revocation marker the logout endpoint reads: only the request whose
            // revocation actually ended a live session may report an upstream sign-out.
            context.Items[AdminAuthenticationSource.RevokedLiveSessionItemKey] = true;
        }
    }

    /// <summary>
    /// The periodic sweep of expired rows, driven by <see cref="AdminSessionCleanupService"/>
    /// (the framework handler has no sweep hook of its own). The bounded SQLite delete is safe
    /// to wait on the background thread (no synchronization context). A storage failure is
    /// contained here — logged with the fixed diagnostic, never SQL or provider detail — so the
    /// host survives it and the next interval retries.
    /// </summary>
    public int RemoveExpired(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<AdminSessionStore>();
            return store.CleanupAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is AdminSessionStorageException or StorageBusyException)
        {
            logger.LogWarning("Expired administrator session cleanup failed; the next batch will retry.");
            return 0;
        }
    }

    private async Task<string> ReplaceSessionAsync(string? oldKey, AuthenticationTicket ticket,
        HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        cancellationToken.ThrowIfCancellationRequested();
        // The handler reaches this point after its ID-token validation and the pre-sign-in
        // administrator gate; the access-token re-verification below is the store's own
        // boundary, so a failure here fails the sign-in closed instead of persisting a ticket
        // whose roles cannot be re-established.
        var accessToken = ticket.Properties.GetTokenValue("access_token");
        using var scope = scopeFactory.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AdminAccessTokenValidator>();
        var accessPrincipal = string.IsNullOrEmpty(accessToken)
            ? null
            : await validator.ValidateAsync(accessToken, cancellationToken);
        if (accessPrincipal is null
            || !VocabularyClaims.HasRole(accessPrincipal, adminOptions.CurrentValue.RequiredRole))
        {
            throw new InvalidOperationException(
                "A hosted sign-in reached session storage without a re-verifiable administrator access token.");
        }

        var expiresAt = ticket.Properties.ExpiresUtc
            ?? throw new InvalidOperationException(
                "A hosted sign-in reached session storage without a bounded expiry.");
        // The validated ID-token principal omits the consumed iss claim; the token-validated
        // event carried the verified issuer on the properties instead.
        var issuer = ticket.Principal.FindFirst("iss")?.Value ?? string.Empty;
        if (issuer.Length == 0
            && ticket.Properties.Items.TryGetValue(AdminAuthenticationSource.OidcIssuerPropertiesKey, out var verifiedIssuer))
        {
            issuer = verifiedIssuer ?? string.Empty;
        }

        var session = new ValidatedAdminSession
        {
            AccessToken = accessToken!,
            IdToken = ticket.Properties.GetTokenValue("id_token"),
            Issuer = issuer,
            Subject = ticket.Principal.FindFirst("sub")?.Value ?? string.Empty,
            DisplayName = VocabularyClaims.GetDisplayName(accessPrincipal),
            Roles = VocabularyClaims.GetRoles(accessPrincipal).ToArray(),
            AccessTokenExpiresAt = expiresAt
        };
        // Built before any session write: an operator identity the audit model rejects fails
        // the sign-in closed rather than issuing a session whose security event cannot be
        // recorded. The row joins the storage transaction below.
        var auditWrite = scope.ServiceProvider.GetRequiredService<AdminAuthenticationAudit>()
            .StageLoginSucceeded(context, accessPrincipal);
        var store = scope.ServiceProvider.GetRequiredService<AdminSessionStore>();
        return oldKey is null
            ? await store.ReplaceAsync(null, session, auditWrite, cancellationToken)
            : await store.ReplaceInPlaceAsync(oldKey, session, auditWrite, cancellationToken);
    }

    private static AuthenticationTicket ToTicket(ValidatedAdminSession session)
    {
        // The identity carries the short OIDC claim names and the cookie session scheme as its
        // authentication type, so the re-presented principal is authenticated and the
        // session-identity middleware can mint the management identity from it. The stored
        // tokens ride back on the ticket's properties so the standard RP-initiated logout can
        // present the ID-token hint without a second storage read.
        List<Claim> claims = [new("iss", session.Issuer), new("sub", session.Subject)];
        if (!string.IsNullOrWhiteSpace(session.DisplayName)) claims.Add(new("name", session.DisplayName));
        claims.AddRange(session.Roles.Select(role => new Claim("role", role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims, AdminAuthenticationSource.SessionScheme, "name", "role"));
        var properties = new AuthenticationProperties
        {
            ExpiresUtc = session.AccessTokenExpiresAt,
            IsPersistent = true
        };
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = session.AccessToken },
            new AuthenticationToken { Name = "id_token", Value = session.IdToken ?? string.Empty },
            new AuthenticationToken { Name = "expires_at", Value = session.AccessTokenExpiresAt.ToString("o", CultureInfo.InvariantCulture) }
        ]);
        return new AuthenticationTicket(principal, properties, AdminAuthenticationSource.SessionScheme);
    }
}
