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
    public string? LastAppId { get; set; }
    public string? LastAppSecret { get; set; }

    public void Reset()
    {
        Mode = FakeIdentityMode.AdminSuccess;
        LastRequestBody = null;
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
        _state.LastAppId = GetHeader(request, "X-Admin-AppId");
        _state.LastAppSecret = GetHeader(request, "X-Admin-AppSecret");

        if (_state.Mode == FakeIdentityMode.Unavailable)
        {
            throw new HttpRequestException("Identity is unavailable.");
        }

        if (_state.Mode == FakeIdentityMode.InvalidCredentials)
        {
            return Json(new
            {
                success = false,
                message = "authentication_failed"
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

    private static HttpResponseMessage Json(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };
    }
}
