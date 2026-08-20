using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The two anonymous surfaces are rate limited per client address. Every test
/// here sets a client address explicitly, because the interesting property is
/// not that requests are refused but that they are refused per caller: a limit
/// that turns out to be global is itself a way to lock the administrator out.
/// </summary>
public class RateLimitingTests
{
    private const string ClientA = "203.0.113.10";
    private const string ClientB = "203.0.113.11";

    [Fact]
    public async Task AdminLogin_BeyondPermitLimit_Returns429Envelope()
    {
        using var factory = CreateFactory(loginPermits: 3);
        using var client = CreateClient(factory);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var allowed = await LoginAsync(client, ClientA);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var refused = await LoginAsync(client, ClientA);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var envelope = JsonDocument.Parse(body);
        Assert.False(envelope.RootElement.GetProperty("success").GetBoolean());
        Assert.False(
            string.IsNullOrWhiteSpace(envelope.RootElement.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task AdminLogin_Rejection_CarriesRetryAfter()
    {
        using var factory = CreateFactory(loginPermits: 1);
        using var client = CreateClient(factory);

        await LoginAsync(client, ClientA);
        var refused = await LoginAsync(client, ClientA);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        // Without this a well-behaved client can only guess how long to wait, and
        // guessing short is indistinguishable from not backing off at all.
        var retryAfter = Assert.Single(refused.Headers.GetValues("Retry-After"));
        Assert.True(int.Parse(retryAfter) > 0);
    }

    [Fact]
    public async Task AdminLogin_ExhaustedByOneAddress_StillAdmitsAnother()
    {
        using var factory = CreateFactory(loginPermits: 2);
        using var client = CreateClient(factory);

        await LoginAsync(client, ClientA);
        await LoginAsync(client, ClientA);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await LoginAsync(client, ClientA)).StatusCode);

        // The whole point of the partition. A global ceiling would mean anyone
        // able to reach the login form could keep the administrator out of it.
        var other = await LoginAsync(client, ClientB);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, other.StatusCode);
    }

    [Fact]
    public async Task PublicApi_BeyondPermitLimit_Returns429()
    {
        using var factory = CreateFactory(publicApiPermits: 2);
        using var client = CreateClient(factory);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var allowed = await GetPublicAsync(client, ClientA);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        var refused = await GetPublicAsync(client, ClientA);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task PublicApi_UnknownRoute_IsAlsoLimited()
    {
        using var factory = CreateFactory(publicApiPermits: 1);
        using var client = CreateClient(factory);

        // The catch-all answers 404 from the envelope, which is cheap but not
        // free, and it is the obvious surface to hammer once the real routes
        // start refusing.
        await GetAsync(client, "/api/does-not-exist", ClientA);
        var refused = await GetAsync(client, "/api/does-not-exist", ClientA);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_AreNotRateLimited()
    {
        using var factory = CreateFactory(loginPermits: 1, publicApiPermits: 1);
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));

        // An authenticated administrator is not the threat these ceilings answer,
        // and metering the administration UI would break it long before it broke
        // an attacker.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await GetAsync(
                client,
                "/admin/vocabulary-books?page=1&size=20",
                ClientA);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task SpoofedForwardedHeader_DoesNotCreateNewPartitions()
    {
        using var factory = CreateFactory(loginPermits: 2);
        using var client = CreateClient(factory);

        // No trusted proxy is configured, so X-Forwarded-For is untrusted input.
        // Honouring it here would let any caller mint a fresh partition key per
        // request and pass the ceiling without ever reaching it, which is worse
        // than having no ceiling because it looks like one is enforced.
        await LoginAsync(client, ClientA, forwardedFor: "198.51.100.1");
        await LoginAsync(client, ClientA, forwardedFor: "198.51.100.2");
        var refused = await LoginAsync(client, ClientA, forwardedFor: "198.51.100.3");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task ForwardedHeader_FromTrustedProxy_PartitionsOnTheRealClient()
    {
        using var factory = CreateFactory(
            loginPermits: 2,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Network:TrustedProxies:0"] = ClientA
            });
        using var client = CreateClient(factory);

        await LoginAsync(client, ClientA, forwardedFor: "198.51.100.1");
        await LoginAsync(client, ClientA, forwardedFor: "198.51.100.1");
        var sameClient = await LoginAsync(client, ClientA, forwardedFor: "198.51.100.1");
        var otherClient = await LoginAsync(client, ClientA, forwardedFor: "198.51.100.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, sameClient.StatusCode);
        // Two browsers behind one reverse proxy must not share a login budget.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherClient.StatusCode);
    }

    [Fact]
    public async Task DisabledPolicy_AdmitsEveryRequest()
    {
        using var factory = CreateFactory(
            extraConfiguration: new Dictionary<string, string?>
            {
                ["RateLimits:AdminLogin:Enabled"] = "false"
            });
        using var client = CreateClient(factory);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var response = await LoginAsync(client, ClientA);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("0", "300")]
    [InlineData("10", "0")]
    [InlineData("-1", "300")]
    public void InvalidPolicy_FailsStartupRatherThanDisablingTheLimit(
        string permitLimit,
        string windowSeconds)
    {
        using var factory = CreateFactory(
            extraConfiguration: new Dictionary<string, string?>
            {
                ["RateLimits:AdminLogin:PermitLimit"] = permitLimit,
                ["RateLimits:AdminLogin:WindowSeconds"] = windowSeconds
            });

        // A typo in a security ceiling must not be quietly repaired into a value
        // that took effect, and must not wait until the first request that would
        // have been limited to surface.
        var failure = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
        Assert.Contains("RateLimits:AdminLogin", failure.Message);
    }

    private static VocabularyWebApplicationFactory CreateFactory(
        int loginPermits = 100,
        int publicApiPermits = 100,
        IReadOnlyDictionary<string, string?>? extraConfiguration = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["RateLimits:AdminLogin:PermitLimit"] = loginPermits.ToString(),
            ["RateLimits:AdminLogin:WindowSeconds"] = "300",
            ["RateLimits:PublicApi:PermitLimit"] = publicApiPermits.ToString(),
            ["RateLimits:PublicApi:WindowSeconds"] = "300"
        };
        foreach (var entry in extraConfiguration ?? new Dictionary<string, string?>())
        {
            configuration[entry.Key] = entry.Value;
        }

        return new VocabularyWebApplicationFactory(
            "Testing",
            includeAppCredentials: true,
            extraConfiguration: configuration);
    }

    private static HttpClient CreateClient(VocabularyWebApplicationFactory factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    private static Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string clientAddress,
        string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/login")
        {
            Content = JsonContent.Create(new { username = "admin", password = "secret" })
        };
        request.Headers.Add(
            VocabularyWebApplicationFactory.ClientAddressHeader,
            clientAddress);
        if (forwardedFor != null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> GetPublicAsync(
        HttpClient client,
        string clientAddress)
    {
        return GetAsync(client, "/api/vocabulary-books/all", clientAddress);
    }

    private static Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string clientAddress)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            VocabularyWebApplicationFactory.ClientAddressHeader,
            clientAddress);
        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
