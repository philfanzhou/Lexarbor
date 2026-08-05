using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Host.Authentication;

public sealed class AdminAccessTokenValidator
{
    private readonly IOptionsMonitor<JwtBearerOptions> _optionsMonitor;
    private readonly ILogger<AdminAccessTokenValidator> _logger;

    public AdminAccessTokenValidator(
        IOptionsMonitor<JwtBearerOptions> optionsMonitor,
        ILogger<AdminAccessTokenValidator> logger)
    {
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public async Task<ClaimsPrincipal?> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = _optionsMonitor.Get(JwtBearerDefaults.AuthenticationScheme);
            var result = await ValidateOnceAsync(
                accessToken,
                options,
                cancellationToken);
            if (!result.IsValid &&
                result.Exception is SecurityTokenSignatureKeyNotFoundException &&
                options.RefreshOnIssuerKeyNotFound &&
                options.ConfigurationManager != null)
            {
                options.ConfigurationManager.RequestRefresh();
                result = await ValidateOnceAsync(
                    accessToken,
                    options,
                    cancellationToken);
            }

            if (!result.IsValid || result.ClaimsIdentity == null)
            {
                _logger.LogWarning(
                    "Identity access token validation failed: {ExceptionType}",
                    result.Exception?.GetType().Name ?? "InvalidToken");
                return null;
            }

            return new ClaimsPrincipal(result.ClaimsIdentity);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Identity access token validation could not be completed: {ExceptionType}",
                exception.GetType().Name);
            return null;
        }
    }

    private static async Task<TokenValidationResult> ValidateOnceAsync(
        string accessToken,
        JwtBearerOptions options,
        CancellationToken cancellationToken)
    {
        var validationParameters = options.TokenValidationParameters.Clone();
        if (options.ConfigurationManager != null)
        {
            var configuration =
                await options.ConfigurationManager.GetConfigurationAsync(cancellationToken);
            validationParameters.IssuerSigningKeys =
                (validationParameters.IssuerSigningKeys ?? [])
                .Concat(configuration.SigningKeys);
        }

        var handler = new JsonWebTokenHandler
        {
            MapInboundClaims = options.MapInboundClaims
        };
        return await handler.ValidateTokenAsync(accessToken, validationParameters);
    }
}
