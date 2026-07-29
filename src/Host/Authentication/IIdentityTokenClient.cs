namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public interface IIdentityTokenClient
{
    Task<IdentityLoginResult> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken);
}
