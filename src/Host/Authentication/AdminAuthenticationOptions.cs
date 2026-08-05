namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public sealed class AdminAuthenticationOptions
{
    public const string SectionName = "AdminAuthentication";

    public string CookieName { get; set; } = "ruoyuVocabularyAdmin";
    public bool CookieSecure { get; set; }

    /// <summary>
    /// Which credential provider handles administrator login.
    /// </summary>
    public AdminAuthenticationProvider Provider { get; set; } =
        AdminAuthenticationProvider.QuantumZhou;

    /// <summary>
    /// Role required by the <c>VocabularyAdmin</c> policy and by the login endpoint.
    /// </summary>
    public string RequiredRole { get; set; } = "admin";
}

/// <summary>
/// Credentials for the QuantumZhou.Identity provider. Kept out of the shared
/// <c>IdentityService</c> section because that section is published to every service via
/// Consul KV, while these are provider specific secrets injected per deployment.
/// </summary>
public sealed class QuantumZhouProviderOptions
{
    public const string SectionName = "AdminAuthentication:QuantumZhou";

    /// <summary>
    /// Base URL of the token endpoint. Falls back to <c>IdentityService:Authority</c>
    /// when empty, so a deployment that serves login and JWKS from different hosts can
    /// override just this one.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    public string TokenPath { get; set; } = "/api/auth/token";
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
}

/// <summary>
/// Settings for the standard OIDC password-grant provider.
/// </summary>
public sealed class OidcProviderOptions
{
    public const string SectionName = "AdminAuthentication:Oidc";

    /// <summary>
    /// Absolute token endpoint. When empty it is discovered from the JWT bearer
    /// configuration manager, which already caches the provider's discovery document.
    /// </summary>
    public string TokenEndpoint { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = "openid profile";
}
