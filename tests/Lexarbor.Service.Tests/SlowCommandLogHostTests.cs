using Lexarbor.Database;
using Lexarbor.Database.Diagnostics;
using Lexarbor.Learning.Database;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

/// <summary>The host attaches one slow command log to catalog and learner contexts unless it is configured off.</summary>
public sealed class SlowCommandLogHostTests
{
    [Theory]
    [InlineData(null, 500)]
    [InlineData("1200", 1200)]
    public async Task CatalogAndLearnerContextsShareTheConfiguredLog(string? configured, int milliseconds)
    {
        await using var factory = Factory(configured);
        _ = factory.CreateClient();
        var services = factory.Services;

        var log = Assert.Single(services.GetServices<SlowCommandLog>());
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), log.Threshold);
        await using var catalog = await services.GetRequiredService<IDbContextFactory<VocabularyDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Contains(log, Interceptors(catalog.GetService<IDbContextOptions>()));
        Assert.Contains(log, Interceptors(services.GetRequiredService<LearningStorage>().Options("caller")));
    }

    [Fact]
    public async Task ZeroTurnsTheLogOff()
    {
        await using var factory = Factory("0");
        _ = factory.CreateClient();
        var services = factory.Services;

        Assert.Empty(services.GetServices<SlowCommandLog>());
        await using var catalog = await services.GetRequiredService<IDbContextFactory<VocabularyDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Interceptors(catalog.GetService<IDbContextOptions>()), interceptor => interceptor is SlowCommandLog);
        Assert.Empty(Interceptors(services.GetRequiredService<LearningStorage>().Options("caller")));
    }

    [Fact]
    public async Task AnInvalidThresholdStopsStartup()
    {
        await using var factory = Factory("-5");

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    private static VocabularyWebApplicationFactory Factory(string? threshold) => new(
        "Testing", includeAppCredentials: true,
        extraConfiguration: new Dictionary<string, string?> { [SlowCommandLog.ThresholdKey] = threshold });

    private static IEnumerable<object> Interceptors(IDbContextOptions options) =>
        options.FindExtension<CoreOptionsExtension>()?.Interceptors ?? [];
}
