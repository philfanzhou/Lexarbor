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

    public async Task<string> ReplaceAsync(string? oldHandle, ValidatedAdminSession session,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        await repository.CreateOrReplaceAsync(row, Hash(oldHandle), cancellationToken);
        return handle;
    }

    public async Task<ValidatedAdminSession?> ReadAsync(string? handle, CancellationToken cancellationToken = default)
    {
        var hash = Hash(handle);
        if (hash is null) return null;
        return Recover(await repository.ReadAsync(hash, cancellationToken), hash);
    }

    public async Task<ValidatedAdminSession?> RevokeAndReadAsync(string? handle, CancellationToken cancellationToken = default)
    {
        var hash = Hash(handle);
        if (hash is null) return null;
        // Delete and confirm commit before decrypting. Corrupt/expired records still get removed.
        return Recover(await repository.RevokeAndReadAsync(hash, cancellationToken), hash);
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
