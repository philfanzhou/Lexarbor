using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Host.RateLimiting;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Web.RateLimiting;

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
    public async Task AdminLogin_BeyondPermitLimit_Returns429ProblemDetails()
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
        await AssertProblemAsync(refused);
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

        await AssertProblemAsync(refused);
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
    public async Task PublicApi_BookBrowseRoutes_AreMeteredByThePublicApiPolicy()
    {
        using var factory = CreateFactory(publicApiPermits: 2);
        using var client = CreateClient(factory);

        // The browse routes are anonymous reads like the catalogue route, so
        // they draw on the same per-address budget rather than a separate one.
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await GetAsync(client, "/api/vocabulary-books/some-book/units", ClientA)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await GetAsync(client, "/api/vocabulary-books/some-book/entries", ClientA)).StatusCode);

        await AssertProblemAsync(await GetAsync(client, "/api/vocabulary-books/some-book/units", ClientA));
        Assert.NotEqual(HttpStatusCode.TooManyRequests,
            (await GetAsync(client, "/api/vocabulary-books/some-book/units", ClientB)).StatusCode);
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
    public async Task ForwardedHeader_WithoutMatchingProto_DoesNotChangeClientPartition()
    {
        using var factory = CreateFactory(
            loginPermits: 1,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Network:TrustedProxies:0"] = ClientA
            });
        using var client = CreateClient(factory);

        await LoginAsync(client, ClientA, forwardedFor: "198.51.100.1", includeForwardedProto: false);
        var refused = await LoginAsync(client, ClientA, forwardedFor: "198.51.100.2", includeForwardedProto: false);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task ForwardedHeader_UsesOnlyConfiguredTrustedHops()
    {
        using var factory = CreateFactory(
            loginPermits: 1,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Network:TrustedProxies:0"] = ClientA,
                ["Network:TrustedProxies:1"] = "198.51.100.2",
                ["Network:ForwardLimit"] = "2"
            });
        using var client = CreateClient(factory);

        await LoginAsync(client, ClientA, forwardedFor: "198.51.100.1, 198.51.100.2", forwardedProto: "https, http");
        var refused = await LoginAsync(client, ClientA, forwardedFor: "198.51.100.1, 198.51.100.2", forwardedProto: "https, http");
        var otherClient = await LoginAsync(client, ClientA, forwardedFor: "198.51.100.3, 198.51.100.2", forwardedProto: "https, http");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherClient.StatusCode);
    }

    [Fact]
    public async Task ForwardedHeader_TrustsConfiguredNetworkAndMappedIpv4Proxy()
    {
        using var factory = CreateFactory(
            loginPermits: 1,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Network:TrustedNetworks:0"] = "203.0.113.0/24"
            });
        using var client = CreateClient(factory);

        await LoginAsync(client, $"::ffff:{ClientA}", forwardedFor: "198.51.100.1");
        var refused = await LoginAsync(client, $"::ffff:{ClientA}", forwardedFor: "198.51.100.1");
        var otherClient = await LoginAsync(client, $"::ffff:{ClientA}", forwardedFor: "198.51.100.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherClient.StatusCode);
    }

    [Theory]
    [InlineData("Network:TrustedProxies:0", "not-an-address")]
    [InlineData("Network:TrustedNetworks:0", "203.0.113.0/not-a-mask")]
    [InlineData("Network:ForwardLimit", "0")]
    [InlineData("Network:ForwardLimit", "11")]
    public void InvalidForwardingConfiguration_FailsStartup(string key, string value)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Network:TrustedProxies:0"] = ClientA
        };
        configuration[key] = value;
        using var factory = CreateFactory(
            extraConfiguration: configuration);

        Assert.ThrowsAny<Exception>(() => CreateClient(factory));
    }

    [Fact]
    public async Task ForwardingCapability_DoesNotExposeManagementRoutes()
    {
        using var factory = CreateFactory(
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Network:TrustedProxies:0"] = ClientA
            });
        using var client = CreateClient(factory);

        var response = await GetAsync(client, "/management/v1/status", ClientA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
    [InlineData("-1", "300")]
    [InlineData("10001", "300")]
    [InlineData("10", "0")]
    [InlineData("10", "-1")]
    [InlineData("10", "9")]
    [InlineData("10", "601")]
    public void InvalidEnabledPolicies_FailSharedStartupValidation(string permits, string seconds)
    {
        foreach (var policy in new[] { "AdminLogin", "PublicApi" })
        {
            using var factory = CreateFactory(extraConfiguration: new Dictionary<string, string?>
            {
                [$"RateLimits:{policy}:PermitLimit"] = permits,
                [$"RateLimits:{policy}:WindowSeconds"] = seconds
            });
            Assert.Throws<RateLimitingConfigurationException>(() => factory.CreateClient());
        }
    }

    [Theory]
    [InlineData("1", "10")]
    [InlineData("10000", "600")]
    public void SharedNumericBoundaries_AreValid(string permits, string seconds)
    {
        using var factory = CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["RateLimits:AdminLogin:PermitLimit"] = permits,
            ["RateLimits:AdminLogin:WindowSeconds"] = seconds,
            ["RateLimits:PublicApi:PermitLimit"] = permits,
            ["RateLimits:PublicApi:WindowSeconds"] = seconds
        });
        using var client = CreateClient(factory);
    }

    [Theory]
    [InlineData("AdminLogin", "/admin/auth/start", "/api/does-not-exist")]
    [InlineData("PublicApi", "/api/does-not-exist", "/admin/auth/start")]
    public async Task DisabledPolicy_IgnoresInvalidValuesAndPreservesOtherPolicy(
        string policy, string disabledPath, string enabledPath)
    {
        using var factory = CreateFactory(loginPermits: 1, publicApiPermits: 1,
            extraConfiguration: new Dictionary<string, string?>
            {
                [$"RateLimits:{policy}:Enabled"] = "false",
                [$"RateLimits:{policy}:PermitLimit"] = "-1",
                [$"RateLimits:{policy}:WindowSeconds"] = "-1"
            });
        using var client = CreateClient(factory);
        for (var i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await GetAsync(client, disabledPath, ClientA)).StatusCode);
        await GetAsync(client, enabledPath, ClientA);
        await AssertProblemAsync(await GetAsync(client, enabledPath, ClientA));
    }

    [Fact]
    public async Task PoliciesAndAddresses_AreIsolatedAndMappedAddressesShareBucket()
    {
        using var factory = CreateFactory(loginPermits: 1, publicApiPermits: 1);
        using var client = CreateClient(factory);
        await GetPublicAsync(client, ClientA);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await LoginAsync(client, ClientA)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await GetPublicAsync(client, ClientB)).StatusCode);
        await AssertProblemAsync(await GetPublicAsync(client, $"::ffff:{ClientA}"));
        await client.GetAsync("/api/unknown", TestContext.Current.CancellationToken);
        await AssertProblemAsync(await client.GetAsync("/api/unknown", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/admin/auth/start")]
    [InlineData("/api/does-not-exist")]
    public async Task ConcurrentSameBucket_AdmitsOnlyPermitCountWithoutQueue(string path)
    {
        using var factory = CreateFactory(loginPermits: 3, publicApiPermits: 3);
        using var client = CreateClient(factory);
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => GetAsync(client, path, ClientA)));
        Assert.Equal(3, responses.Count(response => response.StatusCode != HttpStatusCode.TooManyRequests));
        foreach (var response in responses.Where(response => response.StatusCode == HttpStatusCode.TooManyRequests))
            await AssertProblemAsync(response);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await GetAsync(client, path, ClientB)).StatusCode);
    }

    [Fact]
    public async Task DefaultPolicies_UseProductDefaultsAndFinalOptions()
    {
        using var factory = new VocabularyWebApplicationFactory();
        using var client = CreateClient(factory);
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitOptions>>().Value;
        Assert.True(options.AdminLogin.Enabled);
        Assert.Equal(10, options.AdminLogin.PermitLimit);
        Assert.Equal(300, options.AdminLogin.WindowSeconds);
        Assert.True(options.PublicApi.Enabled);
        Assert.Equal(300, options.PublicApi.PermitLimit);
        Assert.Equal(60, options.PublicApi.WindowSeconds);
        foreach (var (path, permits, seconds) in new[] { ("/admin/auth/start", 10, 300), ("/api/unknown", 300, 60) })
        {
            for (var i = 0; i < permits; i++)
                Assert.NotEqual(HttpStatusCode.TooManyRequests, (await GetAsync(client, path, ClientA)).StatusCode);
            var response = await GetAsync(client, path, ClientA);
            await AssertProblemAsync(response);
            Assert.Equal(seconds.ToString(), Assert.Single(response.Headers.GetValues("Retry-After")));
        }
    }

    [Theory]
    [InlineData("/admin/auth/callback")]
    [InlineData("/admin/auth/logout/return")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task OtherEndpoints_RemainUnmeteredWithoutRetryHeader(string path)
    {
        using var factory = CreateFactory(loginPermits: 1, publicApiPermits: 1);
        using var client = CreateClient(factory);
        await LoginAsync(client, ClientA);
        await GetPublicAsync(client, ClientA);
        for (var i = 0; i < 3; i++)
        {
            var response = await GetAsync(client, path, ClientA);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.False(response.Headers.Contains("Retry-After"));
        }
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var problem = body.RootElement;
        Assert.Equal(5, problem.EnumerateObject().Count());
        Assert.Equal("urn:servicemantle:error:rate_limit.exceeded", problem.GetProperty("type").GetString());
        Assert.Equal("Too many requests.", problem.GetProperty("title").GetString());
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal("rate_limit.exceeded", problem.GetProperty("errorCode").GetString());
        var correlation = problem.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlation));
        Assert.Equal(correlation, Assert.Single(response.Headers.GetValues("x-correlation-id")));
        Assert.True(int.TryParse(Assert.Single(response.Headers.GetValues("Retry-After")), out var seconds) && seconds > 0);
        Assert.DoesNotContain(ClientA, problem.GetRawText());
        Assert.DoesNotContain("consumer:", problem.GetRawText());
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
        string? forwardedFor = null,
        string? forwardedProto = null,
        bool includeForwardedProto = true)
    {
        // The hosted login start is the anonymous surface the ceiling protects; the
        // status it returns under the limit (503 when unconfigured) is irrelevant
        // here, only the 429 boundary matters.
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/start");
        request.Headers.Add(
            VocabularyWebApplicationFactory.ClientAddressHeader,
            clientAddress);
        if (forwardedFor != null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            if (includeForwardedProto)
            {
                request.Headers.Add("X-Forwarded-Proto", forwardedProto ?? "http");
            }
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
