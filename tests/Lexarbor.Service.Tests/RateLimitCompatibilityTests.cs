using System.Threading.RateLimiting;
using Lexarbor.Host.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ServiceMantle;

namespace Lexarbor.Service.Tests;

public class RateLimitCompatibilityTests
{
    [Theory]
    [InlineData("admin-login", 429, null, "300")]
    [InlineData("public-api", 429, null, "60")]
    [InlineData("admin-login", 429, "7", "7")]
    [InlineData("admin-login", 503, null, null)]
    [InlineData("other-policy", 429, null, null)]
    [InlineData(null, 429, null, null)]
    public async Task RetryAfter_OnlyFillsMissingHeaderForProductPolicyRejections(
        string? policy, int status, string? existing, string? expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<RateLimitOptions>(_ => { });
        await using var app = builder.Build();
        app.UseRouting();
        app.UseLexarborRateLimitRetryAfter();
        var endpoint = app.MapGet("/response", context =>
        {
            context.Response.StatusCode = status;
            if (existing is not null) context.Response.Headers.RetryAfter = existing;
            return context.Response.WriteAsync("response", context.RequestAborted);
        });
        if (policy is not null) endpoint.RequireRateLimiting(policy);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/response", TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(expected, response.Headers.TryGetValues("Retry-After", out var values) ? Assert.Single(values) : null);
    }

    [Fact]
    public async Task SharedRejection_PropagatesWriteCancellationAndLeavesOtherClientsAvailable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceMantle(ServiceId.Parse("lexarbor"), InstanceId.Parse("cancellation-test"))
            .AddLexarborRateLimiting(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<RateLimitOptions>>().Value;
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        using var limiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromSeconds(60),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        });
        using var accepted = limiter.AttemptAcquire();
        using var rejected = limiter.AttemptAcquire();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new CancellationStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, cancellation.Token));

        // The cancellation affects only the rejected response; no persistent
        // writes occur and a separate client bucket can still serve traffic.
        using var factory = new TestInfrastructure.VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/vocabulary-books/all", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class CancellationStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
