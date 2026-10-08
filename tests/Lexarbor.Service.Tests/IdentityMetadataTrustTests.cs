using System.Net;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using Lexarbor.Service.Tests.TestInfrastructure;
using Xunit;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The transport the identity provider's signing metadata is fetched over. The
/// keys published there decide every administration authorization, so a caller
/// able to rewrite that response can issue itself an administrator token.
/// </summary>
public class IdentityMetadataTrustTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void HttpAuthorityOutsideDevelopment_FailsStartup(string environment)
    {
        using var factory = CreateFactory(
            environment,
            new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = "http://identity.test",
                // Unset, so the environment default decides.
                ["IdentityService:RequireHttpsMetadata"] = null
            });

        // Refused at startup rather than on the first administration request:
        // this is a configuration error, and every later request would otherwise
        // answer 500 while the deployment looked healthy.
        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("HTTPS", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpAuthorityInDevelopment_Starts()
    {
        // The Bearer surface's Development default still accepts a plain-HTTP
        // authority; the hosted-login client itself never does (its loopback
        // exception is narrower), so the login stays in optional mode here.
        using var factory = new VocabularyWebApplicationFactory(
            "Development",
            includeAppCredentials: false,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = "http://identity.test",
                ["IdentityService:RequireHttpsMetadata"] = null
            });

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void HttpAuthorityOutsideDevelopment_StartsWhenTheOperatorAsksForIt()
    {
        // The operator's escape hatch keeps serving the Bearer surface's metadata
        // trust decision, but the hosted-login client itself never accepts a plain
        // HTTP authority outside the loopback development/test origins: with login
        // credentials configured, such an authority is now a startup failure.
        Assert.True(Assert.ThrowsAny<Exception>(() =>
        {
            using var factory = CreateFactory(
                "Production",
                new Dictionary<string, string?>
                {
                    ["IdentityService:Authority"] = "http://identity.test",
                    ["IdentityService:RequireHttpsMetadata"] = "false"
                });
            using var client = factory.CreateClient();
            return client;
        }) is OptionsValidationException or InvalidOperationException);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    public void LoopbackAuthorityOutsideDevelopment_Starts(string authority)
    {
        // The placeholder-container shape: no hosted-login credentials at all, so the
        // official client runs in its optional-login mode whatever the placeholder
        // authority says — with credentials configured, the same value is a
        // half-configuration and fails startup instead.
        using var factory = new VocabularyWebApplicationFactory(
            "Production",
            includeAppCredentials: false,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = authority,
                ["IdentityService:RequireHttpsMetadata"] = null
            });

        // Loopback carries no network path to rewrite, and the image ships a
        // loopback placeholder: a container given no identity configuration has
        // to keep serving its public API, which is the same answer absent
        // provider credentials already get.
        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public void HttpsAuthorityOutsideDevelopment_Starts()
    {
        using var factory = CreateFactory(
            "Production",
            new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = "https://identity.test",
                ["IdentityService:RequireHttpsMetadata"] = null
            });

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public async Task ConfiguredAudience_ReachesTheBearerScheme()
    {
        using var factory = CreateFactory(
            "Testing",
            new Dictionary<string, string?>
            {
                ["IdentityService:Audience"] = "some-other-audience"
            });
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/vocabulary-books?page=1&size=5");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateToken("admin"));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // The token carries the default audience. The scheme used to be built
        // from builder.Configuration, which a test host has not contributed to
        // yet, so every IdentityService value a test set was ignored and this
        // request succeeded no matter what the audience said.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static VocabularyWebApplicationFactory CreateFactory(
        string environment,
        IReadOnlyDictionary<string, string?> configuration)
    {
        return new VocabularyWebApplicationFactory(
            environment,
            includeAppCredentials: true,
            extraConfiguration: configuration);
    }
}
