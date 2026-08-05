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
}
