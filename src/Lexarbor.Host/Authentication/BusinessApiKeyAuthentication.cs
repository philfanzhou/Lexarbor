using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Lexarbor.Database.Repositories;
using Lexarbor.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

public sealed class BusinessApiKeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    BusinessApiKeyRepository keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Lexarbor.BusinessApiKey";
    private const string Prefix = "lxk_";
    private const int SecretBytes = 32;

    public static string GenerateKey() => Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SecretBytes));

    public static string HashKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(key)));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(BusinessApiAuthorization.Header, out var values))
            return AuthenticateResult.NoResult();
        if (values.Count != 1 || values[0] is not { } key || key.Length != Prefix.Length + SecretBytes * 2
            || !key.StartsWith(Prefix, StringComparison.Ordinal)
            || key.AsSpan(Prefix.Length).ContainsAnyExcept("0123456789abcdef"))
            return Reject();

        // An indexed lookup by the digest of a 256-bit random secret never sends
        // a caller's credential to SQLite and observes committed revocations.
        var caller = await keys.FindActiveCallerAsync(HashKey(key), Context.RequestAborted);
        if (caller == null) return Reject();

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(BusinessApiAuthorization.CallerClaim, caller.CallerId));
        if (caller.Catalog)
            identity.AddClaim(new Claim(BusinessApiAuthorization.ScopeClaim, BusinessApiAuthorization.Catalog));
        if (caller.MeaningSubmissions)
            identity.AddClaim(new Claim(BusinessApiAuthorization.ScopeClaim, BusinessApiAuthorization.MeaningSubmissions));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private static AuthenticateResult Reject() => AuthenticateResult.Fail("Business API key authentication failed.");

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "ApiKey";
        return VocabularyHttpResponse.WriteFailureAsync(Response, StatusCodes.Status401Unauthorized, "An API key is required.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        VocabularyHttpResponse.WriteFailureAsync(Response, StatusCodes.Status403Forbidden, "The API key lacks the required permission.");
}
