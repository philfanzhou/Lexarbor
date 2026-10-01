using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Lexarbor.Host.Authentication;

/// <summary>Trusted Host callers must validate signature, issuer, audience, exp and administrator role first.</summary>
public sealed class ValidatedAdminSession
{
    public string AccessToken { get; init; } = string.Empty;
    public string? IdToken { get; init; }
    public string Issuer { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string[] Roles { get; init; } = [];
    public DateTimeOffset AccessTokenExpiresAt { get; init; }
}

/// <summary>Internal storage only: no cookie, authentication scheme, refresh or HTTP surface.</summary>
public sealed class AdminSessionStore(AdminSessionRepository repository,
    IDataProtectionProvider protectionProvider, TimeProvider timeProvider)
{
    /// <summary>The stable purpose string for administrator session payloads.</summary>
    public const string AdminSessionPurpose = "Lexarbor.AdminSession.v1";

    private readonly IDataProtector _protector = protectionProvider.CreateProtector(AdminSessionPurpose);

    public Task<string> CreateAsync(ValidatedAdminSession session, CancellationToken cancellationToken = default) =>
        ReplaceAsync(null, session, cancellationToken);

    public Task<string> ReplaceAsync(string? oldHandle, ValidatedAdminSession session,
        CancellationToken cancellationToken = default) =>
        ReplaceAsync(oldHandle, session, null, cancellationToken);

    /// <summary>
    /// Replaces the session, optionally running <paramref name="participatingWrite"/> inside the
    /// same serialized transaction (the sign-in's management audit row), so the companion write
    /// commits with the session or rolls back with it.
    /// </summary>
    public Task<string> ReplaceAsync(string? oldHandle, ValidatedAdminSession session, Func<Task>? participatingWrite,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ReplaceCoreAsync(oldHandle, session, participatingWrite, cancellationToken);
    }

    private async Task<string> ReplaceCoreAsync(string? oldHandle, ValidatedAdminSession session,
        Func<Task>? participatingWrite, CancellationToken cancellationToken)
    {
        if (!IsValid(session) || session.AccessTokenExpiresAt.ToUnixTimeMilliseconds() <= Now())
            throw new ArgumentException("A structurally valid, unexpired, independently verified administrator session is required.", nameof(session));
        var handle = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(handle)!;
        var expiry = session.AccessTokenExpiresAt.ToUnixTimeMilliseconds();
        var payload = new ProtectedSession
        {
            Version = 1,
            HandleHash = hash,
            ExpiresAtUnixMs = expiry,
            Session = new ValidatedAdminSession
            {
                AccessToken = session.AccessToken,
                IdToken = session.IdToken,
                Issuer = session.Issuer,
                Subject = session.Subject,
                DisplayName = session.DisplayName,
                Roles = session.Roles.ToArray(),
                AccessTokenExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiry)
            }
        };
        var row = new AdminSessionEntity
        {
            HandleHash = hash,
            ExpiresAtUnixMs = expiry,
            ProtectedPayload = _protector.Protect(JsonSerializer.Serialize(payload))
        };
        // No retry: if commit has begun and its response is lost, the outcome is unknown.
        await repository.CreateOrReplaceAsync(row, Hash(oldHandle), participatingWrite, cancellationToken);
        return handle;
    }

    public async Task<ValidatedAdminSession?> ReadAsync(string? handle, CancellationToken cancellationToken = default)
    {
        var hash = Hash(handle);
        if (hash is null) return null;
        return Recover(await repository.ReadAsync(hash, cancellationToken), hash);
    }

    public Task<ValidatedAdminSession?> RevokeAndReadAsync(string? handle, CancellationToken cancellationToken = default) =>
        RevokeAndReadAsync(handle, null, cancellationToken);

    /// <summary>
    /// Revokes the session, optionally handing every live session it revoked to
    /// <paramref name="onLiveRevocation"/> inside the same serialized transaction (the logout's
    /// management audit row), so the companion write commits with the revocation or rolls back
    /// with it. The snapshot is still only returned to the caller after the commit is confirmed.
    /// </summary>
    public async Task<ValidatedAdminSession?> RevokeAndReadAsync(string? handle,
        Func<ValidatedAdminSession, Task>? onLiveRevocation, CancellationToken cancellationToken = default)
    {
        var hash = Hash(handle);
        if (hash is null) return null;
        ValidatedAdminSession? snapshot = null;
        // Delete and confirm commit before returning anything; corrupt/expired records still
        // get removed. The payload is decrypted inside the transaction only to decide whether
        // a live session is being revoked — the audit companion write and the revocation then
        // commit together, and a rollback releases both with no snapshot delivered.
        await repository.RevokeAndReadAsync(hash, async row =>
        {
            snapshot = Recover(row, hash);
            if (snapshot is not null && onLiveRevocation is not null) await onLiveRevocation(snapshot);
        }, cancellationToken);
        return snapshot;
    }

    public Task<int> CleanupAsync(CancellationToken cancellationToken = default) =>
        repository.CleanupAsync(Now(), cancellationToken);

    private ValidatedAdminSession? Recover(AdminSessionEntity? row, string hash)
    {
        if (row is null || Now() >= row.ExpiresAtUnixMs) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<ProtectedSession>(_protector.Unprotect(row.ProtectedPayload));
            if (payload is null || payload.Version != 1 || payload.HandleHash != hash
                || payload.ExpiresAtUnixMs != row.ExpiresAtUnixMs || !IsValid(payload.Session)
                || payload.Session!.AccessTokenExpiresAt.ToUnixTimeMilliseconds() != row.ExpiresAtUnixMs
                || Now() >= row.ExpiresAtUnixMs) return null;
            return payload.Session;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private long Now() => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    private static bool IsValid(ValidatedAdminSession? session) => session is not null
        && !string.IsNullOrWhiteSpace(session.AccessToken)
        && !string.IsNullOrWhiteSpace(session.Issuer) && !string.IsNullOrWhiteSpace(session.Subject)
        && (session.IdToken is null || !string.IsNullOrWhiteSpace(session.IdToken))
        && session.Roles is { Length: > 0 } && session.Roles.All(role => !string.IsNullOrWhiteSpace(role));

    private static string? Hash(string? handle)
    {
        // Exactly 32 bytes, canonical unpadded base64url. Reject oversized/alternate selectors early.
        if (handle is null || handle.Length != 43
            || handle.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))) return null;
        byte[] bytes;
        try { bytes = WebEncoders.Base64UrlDecode(handle); }
        catch (FormatException) { return null; }
        if (bytes.Length != 32 || WebEncoders.Base64UrlEncode(bytes) != handle) return null;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(handle)));
    }

    private sealed class ProtectedSession
    {
        public int Version { get; init; }
        public string? HandleHash { get; init; }
        public long ExpiresAtUnixMs { get; init; }
        public ValidatedAdminSession? Session { get; init; }
    }
}
