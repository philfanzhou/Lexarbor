using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lexarbor.Host;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests;

public class SystemVersionEndpointTests
{
    [Theory]
    [InlineData(false, "admin")]
    [InlineData(true, "admin")]
    [InlineData(false, "maintainer")]
    [InlineData(true, "maintainer")]
    public async Task Administrator_ReadsExactSnapshotWithoutCaching(bool cookie, string role)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = role });
        using var client = factory.CreateClient();
        var token = factory.CreateToken(role);
        if (cookie) client.DefaultRequestHeaders.Add("Cookie", $"{VocabularyWebApplicationFactory.CookieName}={token}");
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", "*");
        client.DefaultRequestHeaders.IfModifiedSince = DateTimeOffset.UtcNow.AddDays(1);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var response = await client.GetAsync(SystemVersionEndpoints.Path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertNoStore(response);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            var data = json.RootElement.GetProperty("data");
            Assert.Equal(new[] { "channel", "revision", "version" }, data.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(ApplicationVersion.Current, data.GetProperty("version").GetString());
            Assert.Equal(ApplicationVersion.Revision, data.GetProperty("revision").GetString());
            Assert.Equal(ApplicationVersion.Channel, data.GetProperty("channel").GetString());
            return body;
        }));
        Assert.Single(results.Distinct());
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("malformed", 401)]
    [InlineData("expired", 401)]
    [InlineData("student", 403)]
    [InlineData("admin", 403)]
    public async Task Rejection_ContainsOnlyFailureAndNoStore(string identity, int status)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["AdminAuthentication:RequiredRole"] = "maintainer" });
        using var client = factory.CreateClient();
        string? token = identity switch
        {
            "anonymous" => null,
            "malformed" => "invalid",
            "expired" => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer: VocabularyWebApplicationFactory.Issuer, audience: VocabularyWebApplicationFactory.Audience,
                notBefore: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddHours(-1),
                signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                    VocabularyWebApplicationFactory.SigningSecret)), SecurityAlgorithms.HmacSha256))),
            _ => factory.CreateToken(identity)
        };
        if (token != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync("/ADMIN/system/version/", TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        AssertNoStore(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "message", "success" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task CancelledRead_DoesNotChangeSnapshotOrAnonymousHealth()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(SystemVersionEndpoints.Path, cancellation.Token));
        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "status" }, json.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name));
        Assert.Null(health.Headers.CacheControl);
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
    }
}
