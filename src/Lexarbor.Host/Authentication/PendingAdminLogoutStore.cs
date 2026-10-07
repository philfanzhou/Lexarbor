using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Lexarbor.Host.Authentication;

// Avoid record-generated ToString: these objects contain sensitive correlation material.
public sealed class PendingAdminLogoutStart
{
    public string State { get; }
    public string BrowserBinding { get; }
    public string CookieName { get; }
    public DateTimeOffset ExpiresAt { get; }
    internal PendingAdminLogoutStart(string state, string binding, string cookieName, DateTimeOffset expiresAt)
        => (State, BrowserBinding, CookieName, ExpiresAt) = (state, binding, cookieName, expiresAt);
}

/// <summary>
/// Internal single-instance state for the prepared-logout return trip, separate from
/// authenticated sessions and from pending login transactions. A state is created only
/// while preparing an upstream logout and lives as long as the upstream logout handle
/// (five minutes); the fixed return route consumes it exactly once. Only the hash of a
/// state and the hash of its browser binding are kept. Downstream failure never
/// restores a consumed transaction; a restart discards all of them, which surfaces as
/// the fixed failed-return redirect.
/// </summary>
public sealed class PendingAdminLogoutStore(TimeProvider clock, HostedLoginHttpTestTransport httpTestTransport)
{
    public const int Capacity = 4096;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public PendingAdminLogoutStore(TimeProvider clock) : this(clock, HostedLoginHttpTestTransport.Disabled) { }

    public PendingAdminLogoutStart? Create(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            foreach (var expired in _pending.Where(p => p.Value.Deadline <= now).Select(p => p.Key).ToArray())
                _pending.Remove(expired);
            if (_pending.Count >= Capacity) return null;
            string state;
            string stateHash;
            do { state = Random(); stateHash = Hash(state); } while (_pending.ContainsKey(stateHash));
            var binding = Random();
            var deadline = now + Lifetime;
            cancellationToken.ThrowIfCancellationRequested();
            _pending.Add(stateHash, new Pending(SHA256.HashData(Encoding.ASCII.GetBytes(binding)), deadline));
            return new PendingAdminLogoutStart(state, binding, PendingAdminLogoutCookie.Name(httpTestTransport, state), deadline);
        }
    }

    public bool Consume(string? state, string? browserBinding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Canonical(state) || !Canonical(browserBinding)) return false;
        var stateHash = Hash(state!);
        var bindingHash = SHA256.HashData(Encoding.ASCII.GetBytes(browserBinding!));
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pending.TryGetValue(stateHash, out var pending)) return false;
            if (clock.GetUtcNow() >= pending.Deadline)
            {
                _pending.Remove(stateHash);
                return false;
            }
            // A binding mismatch leaves the transaction alive for the browser that
            // owns it; a copied state alone never consumes or destroys it.
            if (!CryptographicOperations.FixedTimeEquals(bindingHash, pending.BindingHash)) return false;
            _pending.Remove(stateHash);
            return true;
        }
    }

    /// <summary>
    /// The upstream contract allows 22–128 characters from <c>[A-Za-z0-9._~-]</c>; this
    /// service only ever issues, and only ever accepts back, its own canonical
    /// 43-character base64url form (32 random bytes), so anything else cannot match a
    /// stored hash and is rejected before any lookup or cookie name is formed.
    /// </summary>
    internal static bool Canonical(string? value)
    {
        if (value is null || value.Length != 43 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(value);
            return bytes.Length == 32 && WebEncoders.Base64UrlEncode(bytes) == value;
        }
        catch (FormatException) { return false; }
    }
    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    private sealed class Pending(byte[] bindingHash, DateTimeOffset deadline)
    {
        public byte[] BindingHash { get; } = bindingHash;
        public DateTimeOffset Deadline { get; } = deadline;
    }
}

/// <summary>Cookie metadata for the logout return trip; no cookie is written here.</summary>
public static class PendingAdminLogoutCookie
{
    public static string Name(HostedLoginHttpTestTransport transport, string state)
    {
        if (!PendingAdminLogoutStore.Canonical(state)) throw new ArgumentException("A canonical logout state is required.", nameof(state));
        return transport.CookieName("__Host-Lexarbor.Logout." + state);
    }
    // Use these same Path/Secure/Domain attributes when deleting only this transaction's cookie.
    public static CookieOptions Attributes(HostedLoginHttpTestTransport transport) => new()
    {
        HttpOnly = true,
        Secure = !transport.Enabled,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Domain = null
    };
    public static CookieOptions ForStart(HostedLoginHttpTestTransport transport, PendingAdminLogoutStart start)
    {
        var options = Attributes(transport);
        options.Expires = start.ExpiresAt;
        options.MaxAge = PendingAdminLogoutStore.Lifetime;
        return options;
    }
}
