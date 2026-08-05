using System.Net;
using System.Text;
using System.Text.Json;

namespace Ruoyu.Study.Vocabulary.Service.Tests.TestInfrastructure;

public enum FakeIdentityMode
{
    AdminSuccess,
    RegularUserSuccess,
    InvalidCredentials,
    Unavailable
}

public sealed class FakeIdentityState
{
    public FakeIdentityMode Mode { get; set; } = FakeIdentityMode.AdminSuccess;
    public string AccessToken { get; set; } = string.Empty;
    public string? LastRequestBody { get; set; }
    public string? LastRequestUri { get; set; }
    public string? LastContentType { get; set; }
    public string? LastAppId { get; set; }
    public string? LastAppSecret { get; set; }

    public void Reset()
    {
        Mode = FakeIdentityMode.AdminSuccess;
        LastRequestBody = null;
        LastRequestUri = null;
        LastContentType = null;
        LastAppId = null;
        LastAppSecret = null;
    }
}

public sealed class FakeIdentityHandler : HttpMessageHandler
{
    private readonly FakeIdentityState _state;

    public FakeIdentityHandler(FakeIdentityState state)
    {
        _state = state;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _state.LastRequestBody = request.Content == null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        _state.LastRequestUri = request.RequestUri?.ToString();
        _state.LastContentType = request.Content?.Headers.ContentType?.MediaType;
        _state.LastAppId = GetHeader(request, "X-Admin-AppId");
        _state.LastAppSecret = GetHeader(request, "X-Admin-AppSecret");

        if (_state.Mode == FakeIdentityMode.Unavailable)
        {
            throw new HttpRequestException("Identity is unavailable.");
        }

        // A form-encoded body means the OIDC password grant; JSON means QuantumZhou's
        // proprietary envelope. Answering in the wrong dialect would let a provider pass
        // its tests against a contract it does not actually speak.
        var isOidcGrant = string.Equals(
            _state.LastContentType,
            "application/x-www-form-urlencoded",
            StringComparison.OrdinalIgnoreCase);

        if (_state.Mode == FakeIdentityMode.InvalidCredentials)
        {
            return isOidcGrant
                ? Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest)
                : Json(new { success = false, message = "authentication_failed" });
        }

        if (isOidcGrant)
        {
            return Json(new
            {
                access_token = _state.AccessToken,
                token_type = "Bearer",
                expires_in = 3600,
                refresh_token = "identity-refresh-token"
            });
        }

        var roles = _state.Mode == FakeIdentityMode.AdminSuccess
            ? new[] { "admin" }
            : new[] { "student" };
        return Json(new
        {
            success = true,
            message = "Login successful",
            accessToken = _state.AccessToken,
            refreshToken = "identity-refresh-token",
            expiresIn = 3600,
            userInfo = new
            {
                userId = "identity-user",
                username = "test-user",
                roles
            }
        });
    }

    private static string? GetHeader(HttpRequestMessage request, string name)
    {
        return request.Headers.TryGetValues(name, out var values)
            ? values.SingleOrDefault()
            : null;
    }

    private static HttpResponseMessage Json(
        object value,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };
    }
}
