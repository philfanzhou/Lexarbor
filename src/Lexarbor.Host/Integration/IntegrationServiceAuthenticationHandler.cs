using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Encodings.Web;
using Lexarbor.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Host.Integration;

/// <summary>
/// Verifies the short-lived RS256 bearer token an integration client signs for
/// each request: a registered key id, the configured issuer, audience and
/// subject, single iat/nbf/exp claims, a lifetime of at most 60 seconds and five
/// seconds of clock skew. Administrator credentials never authenticate here.
/// </summary>
public sealed class IntegrationServiceAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IntegrationServiceTrust _trust;

    public IntegrationServiceAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IntegrationServiceTrust trust)
        : base(options, logger, encoder) => _trust = trust;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!_trust.IsConfigured || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        try
        {
            var encoded = authorization[7..];
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 8192 };
            var untrusted = handler.ReadJwtToken(encoded);
            if (untrusted.Header.Alg != SecurityAlgorithms.RsaSha256
                || untrusted.Header.Kid is null
                || !_trust.Keys.TryGetValue(untrusted.Header.Kid, out var key))
            {
                return Rejected();
            }

            var principal = handler.ValidateToken(encoded, new TokenValidationParameters
            {
                ValidIssuer = _trust.Issuer,
                ValidAudience = _trust.Audience,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(5),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                TryAllIssuerSigningKeys = false
            }, out var verified);
            var token = (JwtSecurityToken)verified;
            var issued = Time(token, "iat");
            var notBefore = Time(token, "nbf");
            var expires = Time(token, "exp");
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (token.Subject != _trust.Subject
                || token.Claims.Count(claim => claim.Type == "sub") != 1
                || issued <= 0 || notBefore <= 0 || expires <= issued
                || expires - issued > 60 || issued > now + 5 || notBefore > expires)
            {
                return Rejected();
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
        catch (Exception error) when (error is SecurityTokenException or ArgumentException or FormatException or InvalidOperationException)
        {
            return Rejected();
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return VocabularyHttpResponse.WriteFailureAsync(
            Response, StatusCodes.Status401Unauthorized, "Integration authentication is required.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        VocabularyHttpResponse.WriteFailureAsync(
            Response, StatusCodes.Status403Forbidden, "Integration scope is required.");

    private static long Time(JwtSecurityToken token, string name)
    {
        var claims = token.Claims.Where(claim => claim.Type == name).ToArray();
        return claims.Length == 1
               && long.TryParse(claims[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static Task<AuthenticateResult> Rejected() =>
        Task.FromResult(AuthenticateResult.Fail("Integration authentication failed."));
}
