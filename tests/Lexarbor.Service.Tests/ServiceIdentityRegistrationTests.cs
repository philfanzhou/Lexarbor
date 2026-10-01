using Lexarbor.Host;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Web.Logging;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The ServiceMantle host identity is registered in every deployment, with or
/// without a trusted proxy, so later capabilities can build on it. These tests
/// pin that registration and the Service:InstanceId configuration contract.
/// </summary>
public class ServiceIdentityRegistrationTests
{
    [Fact]
    public void WithoutTrustedProxy_ServiceLogContextIsRegisteredWithHostIdentity()
    {
        using var factory = new VocabularyWebApplicationFactory(
            "Testing",
            includeAppCredentials: true);

        using var client = factory.CreateClient();

        var logContext = factory.Services.GetRequiredService<ServiceLogContext>();
        Assert.Equal("lexarbor", logContext.ServiceName);
        Assert.Equal(ApplicationVersion.Current, logContext.ServiceVersion);
        Assert.StartsWith("lexarbor-", logContext.InstanceId, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredInstanceId_IsUsedVerbatim()
    {
        using var factory = new VocabularyWebApplicationFactory(
            "Testing",
            includeAppCredentials: true,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Service:InstanceId"] = "lexarbor-prod-a"
            });

        using var client = factory.CreateClient();

        var logContext = factory.Services.GetRequiredService<ServiceLogContext>();
        Assert.Equal("lexarbor", logContext.ServiceName);
        Assert.Equal(ApplicationVersion.Current, logContext.ServiceVersion);
        Assert.Equal("lexarbor-prod-a", logContext.InstanceId);
    }

    [Theory]
    [InlineData("bad\u0007id")] // control character
    [InlineData("   ")] // whitespace only after trimming
    public void InvalidInstanceId_FailsStartupWithoutEchoingTheValue(string invalidValue)
    {
        using var factory = new VocabularyWebApplicationFactory(
            "Testing",
            includeAppCredentials: true,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Service:InstanceId"] = invalidValue
            });

        var exception = Record.Exception(() => factory.CreateClient());

        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("Service:InstanceId", invalidOperation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidValue, invalidOperation.Message, StringComparison.Ordinal);
    }
}
