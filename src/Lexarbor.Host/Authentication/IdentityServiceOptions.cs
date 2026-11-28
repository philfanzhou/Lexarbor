namespace Lexarbor.Host.Authentication;

/// <summary>
/// Which token issuer this service trusts. The section can be supplied by appsettings,
/// environment variables, or another standard ASP.NET Core configuration provider.
///
/// The hosted-login client credentials deliberately live elsewhere — see
/// <see cref="OidcCodeOptions"/> — because they are per-deployment secrets and do
/// not belong in shared defaults.
/// </summary>
public sealed class IdentityServiceOptions
{
    public const string SectionName = "IdentityService";

    public string Authority { get; set; } = "http://localhost:8080";
    public string Issuer { get; set; } = "http://localhost:8080";
    public string Audience { get; set; } = "lexarbor";

    /// <summary>
    /// Whether the signing metadata may only be fetched over HTTPS. Left unset it
    /// is required everywhere except the Development and Testing environments,
    /// whose identity provider is a local HTTP one.
    /// </summary>
    /// <remarks>
    /// The keys published at <see cref="Authority"/> decide every administration
    /// authorization this service makes, so a caller who can rewrite that
    /// response can issue itself an administrator token. Setting this to false
    /// outside development is therefore a deliberate statement that the path to
    /// the identity provider is trusted; the startup log says so on every start,
    /// and it exists as a value an operator writes rather than as a constant
    /// nobody can see or change.
    /// </remarks>
    public bool? RequireHttpsMetadata { get; set; }

    /// <summary>
    /// The one deployment statement both trusts share — the Bearer scheme and the hosted-login
    /// client — on whether the provider's signing metadata may be fetched over plain HTTP.
    /// A configured value decides; otherwise only Development and Testing, or a loopback
    /// authority, are exempt.
    /// </summary>
    internal static bool RequiresHttpsMetadata(string? authority, bool? configured, IHostEnvironment environment)
    {
        if (configured.HasValue)
        {
            return configured.Value;
        }

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return false;
        }

        // What the requirement protects is a network path an attacker could rewrite,
        // and loopback is not one. Exempting it also keeps a container that has not
        // been given an identity provider starting and serving its public API: the
        // image's placeholder authority is http://localhost:8080, and refusing to
        // start over an unconfigured administration login would be a harsher answer
        // than the one absent provider credentials already get, which is to log and
        // return 503 from the login endpoint.
        return !(Uri.TryCreate(authority, UriKind.Absolute, out var uri) && uri.IsLoopback);
    }
}
