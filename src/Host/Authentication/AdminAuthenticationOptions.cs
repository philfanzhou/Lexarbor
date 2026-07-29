namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public sealed class AdminAuthenticationOptions
{
    public const string SectionName = "AdminAuthentication";

    public string CookieName { get; set; } = "vocabularyAdminAccessToken";
    public bool CookieSecure { get; set; } = true;
}
