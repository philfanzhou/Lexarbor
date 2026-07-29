namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public sealed class IdentityServiceOptions
{
    public const string SectionName = "IdentityService";

    public string Authority { get; set; } = "http://localhost:5002";
    public string Issuer { get; set; } = "QuantumZhou.Identity";
    public string Audience { get; set; } = "QuantumZhou.microservices";
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
}
