namespace Lexarbor.Host.Authentication;

/// <summary>
/// Which token issuer this service trusts. The section can be supplied by appsettings,
/// environment variables, or another standard ASP.NET Core configuration provider.
///
/// Provider credentials deliberately live elsewhere — see
/// <see cref="GatewayProviderOptions"/> and <see cref="OidcProviderOptions"/> — because
/// they are per-deployment secrets and do not belong in shared defaults.
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
}
