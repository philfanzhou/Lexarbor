using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lexarbor.Service.Tests.TestInfrastructure;

namespace Lexarbor.Service.Tests;

public sealed class TextbookAudioRemovalEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedRoutes_UseExistingNotFoundAndAdminAuthorization(bool enabled)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Audio:Enabled"] = enabled.ToString(),
                ["Audio:InternalEndpoint"] = "http://127.0.0.1:1",
                ["Audio:PublicEndpoint"] = "https://audio.example.test/",
                ["Audio:AccessKey"] = "synthetic-access",
                ["Audio:SecretKey"] = "synthetic-secret"
            });
        using var client = factory.CreateClient();
        const string catalog = "/vocabulary-books/book/units/unit/audio-tracks";
        foreach (var suffix in new[] { "", "/track/playback" })
        {
            using var response = await client.GetAsync("/api" + catalog + suffix, Ct);
            await AssertNotFoundAsync(response, "API endpoint was not found.");
        }

        using (var unauthorized = await client.GetAsync("/admin" + catalog, Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("reader"));
        using (var forbidden = await client.GetAsync("/admin" + catalog, Ct))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateToken("admin"));
        foreach (var (method, suffix) in new[]
                 {
                     (HttpMethod.Get, ""), (HttpMethod.Post, ""), (HttpMethod.Put, "/track"),
                     (HttpMethod.Delete, "/track"), (HttpMethod.Get, "/upload-policy"),
                     (HttpMethod.Put, "/track/content"), (HttpMethod.Get, "/track/playback")
                 })
        {
            using var request = new HttpRequestMessage(method, "/admin" + catalog + suffix);
            using var response = await client.SendAsync(request, Ct);
            await AssertNotFoundAsync(response, "Admin endpoint was not found.");
        }
        // The retained pronunciation surface is still registered, even with no target.
        using var pronunciation = await client.GetAsync("/admin/vocabulary/missing/audio", Ct);
        Assert.Equal(HttpStatusCode.NotFound, pronunciation.StatusCode);
        Assert.DoesNotContain("Admin endpoint was not found.", await pronunciation.Content.ReadAsStringAsync(Ct));
    }

    private static async Task AssertNotFoundAsync(HttpResponseMessage response, string message)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(message, body.RootElement.GetProperty("message").GetString());
    }
}
