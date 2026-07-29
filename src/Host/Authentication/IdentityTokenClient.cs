using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ruoyu.Study.Vocabulary.Host.Authentication;

public sealed class IdentityTokenClient : IIdentityTokenClient
{
    public const string HttpClientName = "VocabularyIdentity";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IdentityServiceOptions _options;
    private readonly ILogger<IdentityTokenClient> _logger;

    public IdentityTokenClient(
        IHttpClientFactory httpClientFactory,
        IOptions<IdentityServiceOptions> options,
        ILogger<IdentityTokenClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IdentityLoginResult> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/token")
        {
            Content = JsonContent.Create(
                new IdentityTokenRequest
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
                return new IdentityLoginResult(IdentityLoginStatus.InvalidCredentials);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Identity login returned status {StatusCode}",
                    (int)response.StatusCode);
                return new IdentityLoginResult(IdentityLoginStatus.Unavailable);
            }

            var tokenResponse = await response.Content.ReadFromJsonAsync<IdentityTokenResponse>(
                SerializerOptions,
                cancellationToken);
            if (tokenResponse is not { Success: true } ||
                string.IsNullOrWhiteSpace(tokenResponse.AccessToken) ||
                tokenResponse.UserInfo == null)
            {
                return tokenResponse is { Success: false }
                    ? new IdentityLoginResult(IdentityLoginStatus.InvalidCredentials)
                    : new IdentityLoginResult(IdentityLoginStatus.Unavailable);
            }

            return new IdentityLoginResult(IdentityLoginStatus.Success, tokenResponse);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(
                exception,
                "Identity login request could not be completed");
            return new IdentityLoginResult(IdentityLoginStatus.Unavailable);
        }
    }
}
