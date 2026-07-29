namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public sealed class IdentityTokenRequest
{
    public string GrantType { get; set; } = "password";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
