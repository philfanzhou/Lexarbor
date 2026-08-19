namespace Lexarbor.Host.RateLimiting;

/// <summary>
/// Which upstream hops, if any, are allowed to tell Lexarbor who the client is.
///
/// This exists because of the rate limiter. Its partitions are keyed on the
/// client address, and behind a reverse proxy every request arrives from the
/// proxy, so without forwarded headers a per-address limit silently becomes a
/// global one — which is worse than no limit, because one caller can then spend
/// the whole login budget and lock the administrator out.
///
/// The naive fix is worse still. Trusting <c>X-Forwarded-For</c> from anyone lets
/// a caller mint a new partition key per request and pass the limiter unimpeded,
/// so an unconfigured deployment deliberately keeps using the socket address:
/// degrading to the proxy's address is a visible operational problem, while
/// honouring a spoofable header is an invisible security one. Nothing is trusted
/// until an operator names it here.
/// </summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>Individual proxy addresses, for example <c>172.18.0.2</c>.</summary>
    public IList<string> TrustedProxies { get; set; } = [];

    /// <summary>Proxy address ranges in CIDR form, for example <c>172.18.0.0/16</c>.</summary>
    public IList<string> TrustedNetworks { get; set; } = [];

    /// <summary>
    /// How many entries to walk back from the right of <c>X-Forwarded-For</c>,
    /// which must equal the number of trusted hops in front of Lexarbor. One is
    /// the common case of a single reverse proxy. Raising it past the real hop
    /// count hands the extra steps to the client, whose own header content
    /// occupies the left of the list.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    public bool IsConfigured => TrustedProxies.Count > 0 || TrustedNetworks.Count > 0;
}
