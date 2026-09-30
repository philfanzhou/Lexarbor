using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Lexarbor.Host.Authentication;

// Avoid record-generated ToString: these objects contain sensitive correlation material.
public sealed class PendingAdminLoginStart
{
    public string State { get; }
    public string Nonce { get; }
    public string Challenge { get; }
    public string BrowserBinding { get; }
    public string CookieName => PendingAdminLoginCookie.Name(State);
    public DateTimeOffset ExpiresAt { get; }
    internal PendingAdminLoginStart(string state, string nonce, string challenge, string binding, DateTimeOffset expiresAt)
        => (State, Nonce, Challenge, BrowserBinding, ExpiresAt) = (state, nonce, challenge, binding, expiresAt);
}

public sealed class ConsumedAdminLogin
{
    public string Nonce { get; }
    public string Verifier { get; }
    public string ReturnTarget { get; }
    internal ConsumedAdminLogin(string nonce, string verifier, string returnTarget)
        => (Nonce, Verifier, ReturnTarget) = (nonce, verifier, returnTarget);
}

/// <summary>
/// Internal single-instance protocol state, separate from authenticated sessions.
/// Callers validate callback cardinality/issuer before Consume; cancellation/error
/// callbacks also consume. Downstream failure never restores a consumed transaction.
/// </summary>
public sealed class PendingAdminLoginStore(TimeProvider clock)
{
    public const int Capacity = 4096;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    public PendingAdminLoginStart? Create(string? returnRoute = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = AdminLoginReturnTarget.Normalize(returnRoute);
        if (target is null) return null;
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
            var nonce = Random();
            var verifier = Random();
            var binding = Random();
            var deadline = now + Lifetime;
            cancellationToken.ThrowIfCancellationRequested();
            _pending.Add(stateHash, new Pending(SHA256.HashData(Encoding.ASCII.GetBytes(binding)), deadline, nonce, verifier, target));
            var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            return new PendingAdminLoginStart(state, nonce, challenge, binding, deadline);
        }
    }

    public ConsumedAdminLogin? Consume(string? state, string? browserBinding, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Canonical(state) || !Canonical(browserBinding)) return null;
        var stateHash = Hash(state!);
        var bindingHash = SHA256.HashData(Encoding.ASCII.GetBytes(browserBinding!));
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_pending.TryGetValue(stateHash, out var pending)) return null;
            if (clock.GetUtcNow() >= pending.Deadline)
            {
                _pending.Remove(stateHash);
                return null;
            }
            if (!CryptographicOperations.FixedTimeEquals(bindingHash, pending.BindingHash)) return null;
            _pending.Remove(stateHash);
            return new ConsumedAdminLogin(pending.Nonce, pending.Verifier, pending.ReturnTarget);
        }
    }

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
    private sealed class Pending(byte[] bindingHash, DateTimeOffset deadline, string nonce, string verifier, string returnTarget)
    {
        public byte[] BindingHash { get; } = bindingHash;
        public DateTimeOffset Deadline { get; } = deadline;
        public string Nonce { get; } = nonce;
        public string Verifier { get; } = verifier;
        public string ReturnTarget { get; } = returnTarget;
    }
}

/// <summary>Cookie metadata for future HTTP callers; no cookie is written here.</summary>
public static class PendingAdminLoginCookie
{
    public static string Name(string state)
    {
        if (!PendingAdminLoginStore.Canonical(state)) throw new ArgumentException("A canonical login state is required.", nameof(state));
        return "__Host-Lexarbor.Login." + state;
    }
    // Use these same Path/Secure/Domain attributes when deleting only this transaction's cookie.
    public static CookieOptions Attributes() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Domain = null
    };
    public static CookieOptions ForStart(PendingAdminLoginStart start)
    {
        var options = Attributes();
        options.Expires = start.ExpiresAt;
        options.MaxAge = PendingAdminLoginStore.Lifetime;
        return options;
    }
}

public static class AdminLoginReturnTarget
{
    public const string Default = "/#/books";
    private static readonly HashSet<string> Routes = new(StringComparer.Ordinal)
    {
        "/books", "/vocabulary", "/phrases", "/import", "/import/phrase", "/import/batch"
    };
    public static string? Normalize(string? route)
    {
        if (route is null) return Default;
        if (route.Length is 0 or > 256) return null;
        if (route.StartsWith("/#", StringComparison.Ordinal)) route = route[2..];
        if (route.Any(c => c is '%' or '?' or '#' or '\\' || char.IsControl(c))) return null;
        if (Routes.Contains(route)) return "/#" + route;
        var segments = route.Split('/');
        if (segments.Length == 4 && segments[0] == "" && segments[1] == "books" && segments[3] == "words"
            && segments[2].Length is >= 1 and <= 128 && segments[2].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return "/#" + route;
        return null;
    }
}
