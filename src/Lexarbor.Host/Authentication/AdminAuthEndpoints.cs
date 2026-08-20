using System.Security.Claims;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Service;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication;

public static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // The only anonymous endpoint that costs anything to serve: it forwards
        // the submitted credentials to the identity provider, so without a ceiling
        // it is both a password-guessing oracle and a way to point traffic at that
        // provider from an address the provider attributes to Lexarbor.
        app.MapPost("/admin/auth/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingExtensions.AdminLoginPolicy);
        app.MapGet("/admin/auth/session", GetSession)
            .RequireAuthorization("VocabularyAdmin");
        // Deliberately unlimited. Logout only clears a cookie, and an administrator
        // who cannot end a session because someone else exhausted a shared ceiling
        // is a worse outcome than the requests this would have refused.
        app.MapPost("/admin/auth/logout", Logout).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> LoginAsync(
        AdminLoginRequest request,
        HttpContext context,
        IAdminCredentialAuthenticator authenticator,
        AdminAccessTokenValidator accessTokenValidator,
        IOptions<AdminAuthenticationOptions> authenticationOptions,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return VocabularyHttpResponse.BadRequest("Username and password are required.");
        }

        var adminAuthentication = authenticationOptions.Value;
        if (!authenticator.IsConfigured &&
            !environment.IsDevelopment() &&
            !environment.IsEnvironment("Testing"))
        {
            return VocabularyHttpResponse.ServiceUnavailable(
                "Administrator login is not configured.");
        }

        var result = await authenticator.AuthenticateAsync(
            request.Username,
            request.Password,
            cancellationToken);
        if (result.Status == AdminCredentialStatus.InvalidCredentials)
        {
            return VocabularyHttpResponse.Unauthorized("Invalid username or password.");
        }

        if (result.Status != AdminCredentialStatus.Success ||
            string.IsNullOrWhiteSpace(result.AccessToken))
        {
            return VocabularyHttpResponse.BadGateway(
                "The authentication provider is unavailable.");
        }

        var principal = await accessTokenValidator.ValidateAsync(
            result.AccessToken,
            cancellationToken);
        if (principal == null)
        {
            return VocabularyHttpResponse.BadGateway(
                "The authentication provider returned an invalid access token.");
        }

        if (!VocabularyClaims.HasRole(principal, adminAuthentication.RequiredRole))
        {
            return VocabularyHttpResponse.Forbidden("Administrator role is required.");
        }

        context.Response.Cookies.Append(
            adminAuthentication.CookieName,
            result.AccessToken,
            CreateCookieOptions(
                adminAuthentication,
                result.ExpiresIn ?? TimeSpan.FromHours(1)));

        // Every field here comes from the validated token. Whatever the provider claimed
        // about the user in its own response envelope is never echoed back.
        return VocabularyHttpResponse.Ok(new
        {
            username = VocabularyClaims.GetDisplayName(principal) ?? string.Empty,
            roles = VocabularyClaims.GetRoles(principal)
        });
    }

    private static IResult GetSession(ClaimsPrincipal user)
    {
        var username = VocabularyClaims.GetDisplayName(user) ?? string.Empty;
        var roles = VocabularyClaims.GetRoles(user);
        return VocabularyHttpResponse.Ok(new { username, roles });
    }

    private static IResult Logout(
        HttpContext context,
        IOptions<AdminAuthenticationOptions> authenticationOptions)
    {
        var options = authenticationOptions.Value;
        context.Response.Cookies.Delete(
            options.CookieName,
            CreateCookieOptions(options, maxAge: null));
        return VocabularyHttpResponse.Ok();
    }

    private static CookieOptions CreateCookieOptions(
        AdminAuthenticationOptions options,
        TimeSpan? maxAge)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = options.CookieSecure,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = maxAge
        };
    }

    public sealed class AdminLoginRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
