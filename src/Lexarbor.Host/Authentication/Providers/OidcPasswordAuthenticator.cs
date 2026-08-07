using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication.Providers;

/// <summary>
/// Standard OAuth2 resource owner password credentials grant (RFC 6749 §4.3) against any
/// OIDC provider — Keycloak, Authentik, IdentityServer.
///
/// The token endpoint is discovered through the JWT bearer scheme's configuration
/// manager, which already fetches and caches the provider's discovery document for
/// signature validation, so no second discovery cache is introduced.
/// </summary>
public sealed class OidcPasswordAuthenticator : IAdminCredentialAuthenticator
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly OidcProviderOptions _options;
    private readonly IOptionsMonitor<JwtBearerOptions> _jwtBearerOptions;
    private readonly ILogger<OidcPasswordAuthenticator> _logger;

    public OidcPasswordAuthenticator(
        IHttpClientFactory httpClientFactory,
        IOptions<OidcProviderOptions> options,
        IOptionsMonitor<JwtBearerOptions> jwtBearerOptions,
        ILogger<OidcPasswordAuthenticator> logger)
    {
        _httpClient = httpClientFactory.CreateClient(AdminAuthenticationHttpClient.Name);
        _options = options.Value;
        _jwtBearerOptions = jwtBearerOptions;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ClientId);

    public async Task<AdminCredentialResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        string tokenEndpoint;
        try
        {
            tokenEndpoint = await ResolveTokenEndpointAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                "OIDC token endpoint could not be resolved: {ExceptionType}",
                exception.GetType().Name);
            return AdminCredentialResult.Unavailable;
        }

        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            _logger.LogWarning(
                "OIDC provider published no token endpoint and none is configured");
            return AdminCredentialResult.Unavailable;
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = _options.ClientId
        };

        if (!string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            form["client_secret"] = _options.ClientSecret;
        }

        if (!string.IsNullOrWhiteSpace(_options.Scope))
        {
            form["scope"] = _options.Scope;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            // RFC 6749 §5.2 returns 400 for invalid_grant; providers vary on 401.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                return AdminCredentialResult.InvalidCredentials;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "OIDC token request returned status {StatusCode}",
                    (int)response.StatusCode);
                return AdminCredentialResult.Unavailable;
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(
                SerializerOptions,
                cancellationToken);
            if (tokenResponse == null ||
                string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                return AdminCredentialResult.Unavailable;
            }

            return AdminCredentialResult.Succeeded(
                tokenResponse.AccessToken,
                tokenResponse.ExpiresIn > 0
                    ? TimeSpan.FromSeconds(tokenResponse.ExpiresIn)
                    : null);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(
                exception,
                "OIDC token request could not be completed");
            return AdminCredentialResult.Unavailable;
        }
    }

    private async Task<string> ResolveTokenEndpointAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.TokenEndpoint))
        {
            return _options.TokenEndpoint;
        }

        var configurationManager = _jwtBearerOptions
            .Get(JwtBearerDefaults.AuthenticationScheme)
            .ConfigurationManager;
        if (configurationManager == null)
        {
            return string.Empty;
        }

        var configuration = await configurationManager.GetConfigurationAsync(cancellationToken);
        return configuration.TokenEndpoint ?? string.Empty;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public long ExpiresIn { get; set; }
    }
}
