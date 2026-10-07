using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The explicit plain-HTTP transport for hosted administrator login, available only
/// to a single Testing process. An immutable policy is constructed once at startup
/// from the standard configuration sources (<c>AdminAuthentication:HttpTestOrigins</c>,
/// a semicolon-separated allowlist) together with the actual
/// <see cref="IHostEnvironment.EnvironmentName"/>: it is enabled only when the
/// environment is exactly <c>Testing</c> and the parsed allowlist is non-empty, and a
/// non-empty allowlist outside Testing stops startup instead of being ignored. The
/// same policy decides the two callback-origin checks and the names and Secure
/// attribute of the session and transaction cookies; every other guarantee — opaque
/// handles, browser binding, one-time consumption, audits — is unchanged. Plain HTTP
/// provides no confidentiality or integrity: the allowlist accepts only exact
/// RFC1918 IPv4 and IPv6 unique-local literals, never DNS names, loopback,
/// link-local or public addresses, and the network itself remains the operator's
/// responsibility.
/// </summary>
public sealed class HostedLoginHttpTestTransport
{
    public const string OriginsConfigurationKey = "AdminAuthentication:HttpTestOrigins";
    /// <summary>SignaCore's compatible allowlist (#519) accepts at most 32 origins.</summary>
    public const int MaximumOrigins = 32;
    /// <summary>SignaCore's compatible allowlist document is at most 8192 characters.</summary>
    public const int MaximumLength = 8192;
    private const string HttpPrefix = "http://";

    private readonly IReadOnlySet<string> _allowedOrigins;

    private HostedLoginHttpTestTransport(IReadOnlySet<string> allowedOrigins)
    {
        _allowedOrigins = allowedOrigins;
        Enabled = allowedOrigins.Count != 0;
    }

    /// <summary>The policy every deployment without the configuration runs under: nothing is allowed.</summary>
    public static HostedLoginHttpTestTransport Disabled { get; } =
        new(new HashSet<string>(StringComparer.Ordinal));

    public bool Enabled { get; }

    /// <summary>How many canonical origins the allowlist carries; never their values.</summary>
    public int AllowedOriginCount => _allowedOrigins.Count;

    /// <summary>
    /// The cookie name this transport issues: an enabled transport replaces the
    /// <c>__Host-</c> prefix (which a plain-HTTP browser refuses to store) with
    /// <c>HttpTest-</c>, keeping HttpOnly, SameSite=Lax, Path=/ and no Domain while
    /// dropping Secure. Any other name passes through unchanged.
    /// </summary>
    public string CookieName(string secureName) =>
        Enabled && secureName.StartsWith("__Host-", StringComparison.Ordinal)
            ? "HttpTest-" + secureName["__Host-".Length..]
            : secureName;

    /// <summary>
    /// Whether an absolute URI's origin is one of the allowed private-IP HTTP
    /// origins. Canonical, ordinal, exact: scheme, literal IP and effective port
    /// only — no network resolution, no wildcards, no <c>X-Forwarded-*</c> headers.
    /// </summary>
    public bool Allows(Uri uri) =>
        uri.Scheme == "http" && _allowedOrigins.Contains(OriginKey(WithoutBrackets(uri.Host), uri.Port));

    /// <summary>
    /// <see cref="Uri.Host"/> reports an IPv6 literal inside its brackets; the
    /// allowlist keys store the bare literal the same way the parser does.
    /// </summary>
    private static string WithoutBrackets(string host) =>
        host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;

    /// <summary>
    /// The startup construction. Empty configuration yields the disabled policy; a
    /// non-empty allowlist outside the Testing environment fails fast rather than
    /// letting an operator believe the transport was enabled.
    /// </summary>
    public static HostedLoginHttpTestTransport Create(IHostEnvironment environment, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return Disabled;
        if (!string.Equals(environment.EnvironmentName, "Testing", StringComparison.Ordinal))
            throw new InvalidOperationException(NonTestingFailureMessage);
        var origins = ParseOrigins(configured);
        return origins.Count == 0 ? Disabled : new HostedLoginHttpTestTransport(origins);
    }

    /// <summary>Whether the configuration is empty or parses completely; used by startup validation.</summary>
    public static bool ValidOrigins(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return true;
        try
        {
            ParseOrigins(configured);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal const string NonTestingFailureMessage =
        "AdminAuthentication:HttpTestOrigins is a Testing-only setting. The hosted administrator " +
        "login never accepts plain-HTTP private-network callbacks or issues cookies without Secure " +
        "outside the Testing environment; remove the setting, or run the process with the Testing " +
        "environment, or serve the deployment over HTTPS as documented in " +
        "docs/development/Deployment.md.";

    internal const string InvalidOriginsFailureMessage =
        "AdminAuthentication:HttpTestOrigins entries must be exact private-IP HTTP origins: " +
        "http://<literal-IP>:<port> per entry, with an explicit decimal port 1-65535, an RFC1918 " +
        "IPv4 address (10/8, 172.16/12, 192.168/16) in canonical dotted-quad form or an IPv6 " +
        "unique-local address (fc00::/7), and no scheme other than http, userinfo, path, query, " +
        "fragment, DNS name, localhost, wildcard, CIDR, zone or IPv4-alias spelling. See " +
        "docs/development/Deployment.md for the compatible SignaCore setting.";

    /// <summary>
    /// Parses the semicolon-separated allowlist into canonical origin keys
    /// (<c>http://&lt;canonical-IP&gt;:&lt;effective-port&gt;</c>). Duplicate canonical
    /// forms collapse; any entry that is not an exact private-IP HTTP origin — or more
    /// than 32 of them, or more than 8192 characters in total — fails with a fixed
    /// diagnostic that never echoes the configured value.
    /// </summary>
    public static IReadOnlySet<string> ParseOrigins(string? configured)
    {
        var origins = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(configured)) return origins;
        if (configured.Length > MaximumLength) throw new InvalidOperationException(InvalidOriginsFailureMessage);
        foreach (var entry in configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (origins.Count >= MaximumOrigins) throw new InvalidOperationException(InvalidOriginsFailureMessage);
            var origin = ParseOrigin(entry);
            if (origin is null) throw new InvalidOperationException(InvalidOriginsFailureMessage);
            origins.Add(origin);
        }
        return origins;
    }

    private static string? ParseOrigin(string value)
    {
        if (value.Length == 0 || value.Length > 500) return null;
        // Printable ASCII only: no controls, spaces, Unicode, percent-encoding, zones
        // or backslashes. Everything the fixed-origin syntax cannot contain is
        // rejected on the literal text first, so the URI parser only ever sees the
        // one accepted shape.
        if (value.Any(c => c is < '!' or > '~')) return null;
        if (!value.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var authority = value[HttpPrefix.Length..];
        if (authority.Contains('/') || authority.Contains('?') || authority.Contains('#')
            || authority.Contains('@') || authority.Contains('\\') || authority.Contains('%')) return null;
        string host;
        int port;
        if (authority.StartsWith('['))
        {
            // The only bracketed form is [IPv6]:port with an explicit port.
            var end = authority.IndexOf(']');
            if (end < 0) return null;
            host = authority[1..end];
            var suffix = authority[(end + 1)..];
            if (suffix.Length == 0 || suffix[0] != ':') return null;
            if (!TryParsePort(suffix[1..], out port)) return null;
        }
        else
        {
            // A bare host must be an IPv4 literal with exactly one colon: the port.
            var colon = authority.LastIndexOf(':');
            if (colon < 0) return null;
            if (authority.Count(c => c == ':') != 1) return null;
            host = authority[..colon];
            if (!LiteralIPv4(host)) return null;
            if (!TryParsePort(authority[(colon + 1)..], out port)) return null;
        }
        if (!IPAddress.TryParse(host, out var address)) return null;
        if (!AllowedAddress(address)) return null;
        // Canonical key: lowercase scheme, IPAddress-normalized literal, effective
        // port — so an explicit :80 and an omitted port are the same origin, and
        // IPv6 spellings that differ only in case or zeros collapse.
        return OriginKey(address.ToString(), port);
    }

    private static string OriginKey(string host, int port) => HttpPrefix + host + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Canonical dotted-quad only: four segments of one to three decimal digits,
    /// each 0-255, with no leading zeros — the spellings .NET or a stack might read
    /// as octal or hexadecimal aliases are refused rather than canonicalized.
    /// </summary>
    private static bool LiteralIPv4(string host)
    {
        var segments = host.Split('.');
        if (segments.Length != 4) return false;
        foreach (var segment in segments)
        {
            if (segment.Length is < 1 or > 3) return false;
            if (!segment.All(char.IsAsciiDigit)) return false;
            if (segment.Length > 1 && segment[0] == '0') return false;
            if (int.Parse(segment, NumberStyles.None, CultureInfo.InvariantCulture) > 255) return false;
        }
        return true;
    }

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        if (text.Length is < 1 or > 5) return false;
        if (!text.All(char.IsAsciiDigit)) return false;
        if (text.Length > 1 && text[0] == '0') return false;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)) return false;
        return port is >= 1 and <= 65535;
    }

    /// <summary>
    /// RFC1918 IPv4 (10/8, 172.16/12, 192.168/16) and IPv6 unique-local fc00::/7
    /// only. Loopback, link-local, multicast, public and IPv4-mapped addresses are
    /// all refused: loopback HTTP is already covered by the numeric-loopback
    /// exception of the existing callback checks, and everything else is not a
    /// private test network.
    /// </summary>
    private static bool AllowedAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 alone: every other IPv6 range — loopback, link-local,
            // IPv4-mapped (::ffff:0:0/96 starts with zero bytes, not fc) and global
            // addresses included — is refused here.
            return address.IsIPv6UniqueLocal;
        }
        return false;
    }
}
