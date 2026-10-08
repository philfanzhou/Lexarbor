using System.Security.Claims;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Microsoft.Extensions.Options;
using SignaCore.Client.AspNetCore;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The Lexarbor <see cref="ITicketStore"/>: the official hosted-login package's server-side
/// session boundary backed by the existing SQLite administrator session rows. The browser's
/// opaque key is the same canonical 43-character handle the retired in-house implementation
/// issued, and the protected payload keeps the byte-for-byte <c>v1</c> format, so sessions
/// signed in before the migration (and before a rollback) keep authenticating.
/// </summary>
/// <remarks>
/// <para>
/// Storage runs inside a fresh service scope per operation so the scoped
/// <c>AdminSessionRepository</c>/unit-of-work transaction semantics are preserved for a
/// singleton store. The sign-in audit row is staged as the replacement's participating write
/// and the logout audit row as the revocation's companion write, so each security event
/// commits — or rolls back — with exactly the session write it describes (A2).
/// </para>
/// <para>
/// The ID-token principal the package hands over carries identity only; the administrator
/// roles are re-verified from the stored access token with the retained
/// <see cref="AdminAccessTokenValidator"/> before the ticket enters storage, so the session's
/// role claims are never inferred from an unverified source (PS-12: the ID token carries no
/// role).
/// </para>
/// </remarks>
public sealed class AdminSessionTicketStore(
    IServiceScopeFactory scopeFactory,
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<AdminAuthenticationOptions> adminOptions,
    ILogger<AdminSessionTicketStore> logger) : ITicketStore
{
    public async Task<string?> StoreAsync(SignaCoreSessionTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return await ReplaceAsync(null, ticket, cancellationToken);
    }

    public async Task<string?> ReplaceAsync(string? oldKey, SignaCoreSessionTicket ticket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        cancellationToken.ThrowIfCancellationRequested();
        // The package only reaches this point after its strict ID-token validation and the
        // pre-sign-in administrator gate; the access-token re-verification below is the
        // store's own boundary, so a failure here fails the sign-in closed instead of
        // persisting a ticket whose roles cannot be re-established.
        using var scope = scopeFactory.CreateScope();
        var validator = scope.ServiceProvider.GetRequiredService<AdminAccessTokenValidator>();
        var accessPrincipal = await validator.ValidateAsync(ticket.AccessToken, cancellationToken);
        if (accessPrincipal is null
            || !VocabularyClaims.HasRole(accessPrincipal, adminOptions.CurrentValue.RequiredRole))
        {
            throw new InvalidOperationException(
                "A hosted sign-in reached session storage without a re-verifiable administrator access token.");
        }

        var session = new ValidatedAdminSession
        {
            AccessToken = ticket.AccessToken,
            IdToken = ticket.IdToken,
            Issuer = ticket.Principal.FindFirst("iss")?.Value ?? string.Empty,
            Subject = ticket.Principal.FindFirst("sub")?.Value ?? string.Empty,
            DisplayName = VocabularyClaims.GetDisplayName(accessPrincipal),
            Roles = VocabularyClaims.GetRoles(accessPrincipal).ToArray(),
            AccessTokenExpiresAt = ticket.ExpiresUtc
        };
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("A hosted sign-in requires a request context for its audit row.");
        // Built before any session write: an operator identity the audit model rejects fails
        // the sign-in closed rather than issuing a session whose security event cannot be
        // recorded. The row joins the replacement's transaction below.
        var auditWrite = scope.ServiceProvider.GetRequiredService<AdminAuthenticationAudit>()
            .StageLoginSucceeded(http, accessPrincipal);
        var store = scope.ServiceProvider.GetRequiredService<AdminSessionStore>();
        var handle = await store.ReplaceAsync(oldKey, session, auditWrite, cancellationToken);
        // Only a confirmed commit reaches here. Sign-in replaces every credential the
        // browser may hold from a previous session, including the retired
        // password-login JWT cookie.
        http.Response.Cookies.Delete(AdminSessionCookie.LegacyJwtName, AdminSessionCookie.LegacyOptions());
        return handle;
    }

    public async Task<SignaCoreSessionTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AdminSessionStore>();
        var session = await store.ReadAsync(key, cancellationToken);
        return session is null ? null : ToTicket(session);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("A hosted logout requires a request context for its audit row.");
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AdminSessionStore>();
        // The logout audit row joins the revocation's transaction: it is staged only for a
        // live revoked session, and commits or rolls back with that revocation. Concurrent or
        // repeated logouts of the same key find no live session and write nothing.
        await store.RevokeAndReadAsync(
            key,
            scope.ServiceProvider.GetRequiredService<AdminAuthenticationAudit>().StageLogout(http),
            cancellationToken);
    }

    public int RemoveExpired(CancellationToken cancellationToken)
    {
        // The sweep runs on the package's cleanup background thread; the bounded SQLite
        // delete is safe to wait on there (no synchronization context). A storage
        // failure is contained here — logged with the fixed diagnostic, never SQL or
        // provider detail — so the package's sweep loop and the host survive it and the
        // next interval retries, exactly as the retired cleanup service did.
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

    private SignaCoreSessionTicket ToTicket(ValidatedAdminSession session)
    {
        // The identity carries the short OIDC claim names and the package's session scheme
        // as its authentication type, so the re-presented principal is authenticated and the
        // management-identity middleware can recognize the session source.
        List<Claim> claims = [new("iss", session.Issuer), new("sub", session.Subject)];
        if (!string.IsNullOrWhiteSpace(session.DisplayName)) claims.Add(new("name", session.DisplayName));
        claims.AddRange(session.Roles.Select(role => new Claim("role", role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims, SignaCoreHostedLoginDefaults.SessionAuthenticationScheme, "name", "role"));
        return new SignaCoreSessionTicket(
            principal,
            DateTimeOffset.UnixEpoch,
            session.AccessTokenExpiresAt,
            session.AccessToken,
            session.IdToken ?? string.Empty);
    }
}
