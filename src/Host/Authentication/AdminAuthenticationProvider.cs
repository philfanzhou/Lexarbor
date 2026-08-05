namespace Ruoyu.Study.Vocabulary.Host.Authentication;

/// <summary>
/// Selected by <c>AdminAuthentication:Provider</c>. One implementation is registered at
/// startup; there is no runtime switching.
/// </summary>
public enum AdminAuthenticationProvider
{
    /// <summary>
    /// QuantumZhou.Identity's proprietary <c>POST /api/auth/token</c> contract.
    /// </summary>
    QuantumZhou = 0,

    /// <summary>
    /// Standard OAuth2 resource owner password credentials against an OIDC provider.
    /// </summary>
    Oidc = 1
}

public static class AdminAuthenticationHttpClient
{
    /// <summary>
    /// Named client shared by every provider so tests and policies have a single
    /// injection point.
    /// </summary>
    public const string Name = "VocabularyIdentity";
}
