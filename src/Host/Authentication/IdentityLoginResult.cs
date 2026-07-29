namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public enum IdentityLoginStatus
{
    Success,
    InvalidCredentials,
    Unavailable
}

public sealed record IdentityLoginResult(
    IdentityLoginStatus Status,
    IdentityTokenResponse? TokenResponse = null);
