namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public enum AdminCredentialStatus
{
    Success,
    InvalidCredentials,
    Unavailable
}

/// <summary>
/// Outcome of exchanging administrator credentials for an access token.
///
/// Deliberately carries nothing but the token and its lifetime: whatever the provider
/// reports about the user (display name, roles) is unverified until the token itself is
/// validated, so the caller derives all identity information from the validated
/// principal instead.
/// </summary>
public sealed record AdminCredentialResult(
    AdminCredentialStatus Status,
    string? AccessToken = null,
    TimeSpan? ExpiresIn = null)
{
    public static AdminCredentialResult Succeeded(string accessToken, TimeSpan? expiresIn) =>
        new(AdminCredentialStatus.Success, accessToken, expiresIn);

    public static AdminCredentialResult InvalidCredentials { get; } =
        new(AdminCredentialStatus.InvalidCredentials);

    public static AdminCredentialResult Unavailable { get; } =
        new(AdminCredentialStatus.Unavailable);
}

/// <summary>
/// Turns a username and password into an access token issued by whichever identity
/// provider this deployment trusts.
///
/// This is the only seam that knows a provider's wire protocol. Everything downstream —
/// token validation, role checks, cookie issuance — works off the validated JWT and is
/// provider agnostic.
/// </summary>
public interface IAdminCredentialAuthenticator
{
    /// <summary>
    /// False when required provider credentials are absent, letting the service start
    /// and serve its public API while reporting 503 for administrator login.
    /// </summary>
    bool IsConfigured { get; }

    Task<AdminCredentialResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken);
}
