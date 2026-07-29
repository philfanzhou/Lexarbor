namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public sealed class IdentityTokenResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public long ExpiresIn { get; set; }
    public IdentityUserInfo? UserInfo { get; set; }
}
