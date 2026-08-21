using System.Net;
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
        using var factory = CreateFactory(
            "Development",
            new Dictionary<string, string?>
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
        using var factory = CreateFactory(
            "Production",
            new Dictionary<string, string?>
            {
                ["IdentityService:Authority"] = "http://identity.test",
                ["IdentityService:RequireHttpsMetadata"] = "false"
            });

        // The escape hatch exists so that a deployment whose identity provider
        // is reached over a trusted path can say so. It has to be said: the
        // value used to be a constant no deployment could see or change.
        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    public void LoopbackAuthorityOutsideDevelopment_Starts(string authority)
    {
        using var factory = CreateFactory(
            "Production",
            new Dictionary<string, string?>
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
