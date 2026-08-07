using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Authentication.Providers;

/// <summary>
/// Speaks an optional gateway-style token contract:
/// <c>POST /api/auth/token</c> with a camelCase body and the AppId/AppSecret pair in
/// request headers, answering with a <c>success</c> envelope.
///
/// Every detail of that contract is contained here. Nothing outside this class knows the
/// path, header names, or response envelope shape.
/// </summary>
public sealed class GatewayCredentialAuthenticator : IAdminCredentialAuthenticator
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly GatewayProviderOptions _options;
    private readonly ILogger<GatewayCredentialAuthenticator> _logger;

    public GatewayCredentialAuthenticator(
        IHttpClientFactory httpClientFactory,
        IOptions<GatewayProviderOptions> options,
        ILogger<GatewayCredentialAuthenticator> logger)
    {
        _httpClient = httpClientFactory.CreateClient(AdminAuthenticationHttpClient.Name);
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.AppId) &&
        !string.IsNullOrWhiteSpace(_options.AppSecret);

    public async Task<AdminCredentialResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenPath)
        {
            Content = JsonContent.Create(
                new TokenRequest
                {
                    GrantType = "password",
                    Username = username,
                    Password = password
                },
                options: SerializerOptions)
        };

        if (!string.IsNullOrWhiteSpace(_options.AppId))
        {
            request.Headers.Add("X-Admin-AppId", _options.AppId);
        }

        if (!string.IsNullOrWhiteSpace(_options.AppSecret))
        {
            request.Headers.Add("X-Admin-AppSecret", _options.AppSecret);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                return AdminCredentialResult.InvalidCredentials;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Identity login returned status {StatusCode}",
                    (int)response.StatusCode);
                return AdminCredentialResult.Unavailable;
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(
                SerializerOptions,
                cancellationToken);

            // UserInfo is not surfaced to the caller, but its absence still signals a
            // malformed Identity response rather than a credential problem.
            if (tokenResponse is not { Success: true } ||
                string.IsNullOrWhiteSpace(tokenResponse.AccessToken) ||
                tokenResponse.UserInfo == null)
            {
                return tokenResponse is { Success: false }
                    ? AdminCredentialResult.InvalidCredentials
                    : AdminCredentialResult.Unavailable;
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
                "Identity login request could not be completed");
            return AdminCredentialResult.Unavailable;
        }
    }

    private sealed class TokenRequest
    {
        public string GrantType { get; set; } = "password";
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    private sealed class TokenResponse
    {
        public bool Success { get; set; }
        public string AccessToken { get; set; } = string.Empty;
        public long ExpiresIn { get; set; }
        public UserInfoPayload? UserInfo { get; set; }

        /// <summary>
        /// Identity also returns <c>message</c> and <c>refreshToken</c>. They are
        /// intentionally not bound: the message must never reach the client, and the
        /// refresh token is discarded because this service does not renew sessions.
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Ignored { get; set; }
    }

    private sealed class UserInfoPayload
    {
        public string Username { get; set; } = string.Empty;
    }
}
