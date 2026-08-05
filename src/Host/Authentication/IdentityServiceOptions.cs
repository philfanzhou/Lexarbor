namespace Ruoyu.Study.Vocabulary.Host.Authentication;

/// <summary>
/// Which token issuer this service trusts. Published to every microservice through the
/// shared Consul KV blob <c>config/ruoyu/service-endpoints.json</c>, so the section name
/// and keys are a platform-wide contract rather than this service's to rename.
///
/// Provider credentials deliberately live elsewhere — see
/// <see cref="QuantumZhouProviderOptions"/> and <see cref="OidcProviderOptions"/> — because
/// they are per-deployment secrets that never travel through Consul.
/// </summary>
public sealed class IdentityServiceOptions
{
    public const string SectionName = "IdentityService";

    public string Authority { get; set; } = "http://localhost:5002";
    public string Issuer { get; set; } = "QuantumZhou.Identity";
    public string Audience { get; set; } = "QuantumZhou.microservices";
}
