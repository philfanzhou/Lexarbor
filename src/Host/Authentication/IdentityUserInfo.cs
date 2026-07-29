namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public sealed class IdentityUserInfo
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
}
