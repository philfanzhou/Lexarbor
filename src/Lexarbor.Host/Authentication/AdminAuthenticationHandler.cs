using System.Text.Encodings.Web;
using Lexarbor.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

/// <summary>
/// The thin presentation handler behind the administrator policy scheme's challenge
/// and forbid answers: the management API's fixed JSON 401/403 envelope. It never
/// authenticates; the cookie session handler's 302-to-start challenge stays out
/// of the API surface because the policy scheme forwards challenge and forbid here.
/// </summary>
public sealed class AdminAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ApiResponse.WriteFailureAsync(Response, StatusCodes.Status401Unauthorized, "Authentication is required.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ApiResponse.WriteFailureAsync(Response, StatusCodes.Status403Forbidden, "Administrator role is required.");
}
