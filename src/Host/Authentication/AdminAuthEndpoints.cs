using System.Security.Claims;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Vocabulary.Service;

namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public static class AdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/auth/login", LoginAsync).AllowAnonymous();
        app.MapGet("/admin/auth/session", GetSession)
            .RequireAuthorization("VocabularyAdmin");
        app.MapPost("/admin/auth/logout", Logout).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> LoginAsync(
        AdminLoginRequest request,
        HttpContext context,
        IIdentityTokenClient identityTokenClient,
        AdminAccessTokenValidator accessTokenValidator,
        IOptions<IdentityServiceOptions> identityOptions,
        IOptions<AdminAuthenticationOptions> authenticationOptions,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return VocabularyHttpResponse.BadRequest("Username and password are required.");
        }

        var identity = identityOptions.Value;
        if (!environment.IsDevelopment() &&
            !environment.IsEnvironment("Testing") &&
            (string.IsNullOrWhiteSpace(identity.AppId) ||
             string.IsNullOrWhiteSpace(identity.AppSecret)))
        {
            return VocabularyHttpResponse.ServiceUnavailable(
                "Administrator login is not configured.");
        }

        var result = await identityTokenClient.LoginAsync(
            request.Username,
            request.Password,
            cancellationToken);
        if (result.Status == IdentityLoginStatus.InvalidCredentials)
        {
            return VocabularyHttpResponse.Unauthorized("Invalid username or password.");
        }

        if (result.Status == IdentityLoginStatus.Unavailable ||
            result.TokenResponse?.UserInfo == null)
        {
            return VocabularyHttpResponse.BadGateway(
                "Identity service is unavailable.");
        }

        var token = result.TokenResponse;
        var principal = await accessTokenValidator.ValidateAsync(
            token.AccessToken,
            cancellationToken);
        if (principal == null)
        {
            return VocabularyHttpResponse.BadGateway(
                "Identity service returned an invalid access token.");
        }

        if (!principal.IsInRole("admin"))
        {
            return VocabularyHttpResponse.Forbidden("Administrator role is required.");
        }

        var adminAuthentication = authenticationOptions.Value;
        context.Response.Cookies.Append(
            adminAuthentication.CookieName,
            token.AccessToken,
            CreateCookieOptions(
                adminAuthentication,
                token.ExpiresIn > 0
                    ? TimeSpan.FromSeconds(token.ExpiresIn)
                    : TimeSpan.FromHours(1)));

        return VocabularyHttpResponse.Ok(new
        {
            username =
                principal.FindFirstValue("preferred_username") ??
                principal.Identity?.Name ??
                principal.FindFirstValue("sub") ??
                token.UserInfo.Username,
            roles = principal.FindAll("role").Select(claim => claim.Value).ToArray()
        });
    }

    private static IResult GetSession(ClaimsPrincipal user)
    {
        var username =
            user.FindFirstValue("preferred_username") ??
            user.Identity?.Name ??
            user.FindFirstValue("sub") ??
            string.Empty;
        var roles = user.FindAll("role").Select(claim => claim.Value).ToArray();
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
