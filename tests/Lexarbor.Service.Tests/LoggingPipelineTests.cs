using System.Net;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Web.Logging;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The ServiceMantle Serilog console pipeline: identity fields on every event,
/// structured-property sanitization, the request-header projection, category
/// level configuration equivalent to the pre-migration rules, and the product
/// rules that cannot become overrides (request-scoped session suppression and
/// the Data Protection None rule) still holding under the Serilog provider.
/// </summary>
public class LoggingPipelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Out;
        private readonly StringWriter _writer = new();

        public ConsoleCapture() => Console.SetOut(_writer);

        public string Output
        {
            get
            {
                _writer.Flush();
                return _writer.ToString();
            }
        }

        public void Dispose()
        {
            Console.SetOut(_original);
            _writer.Dispose();
        }
    }


    [Fact]
    public async Task ConsoleOutput_CarriesIdentityFieldsAndNeverTheCallbackSecrets()
    {
        using var capture = new ConsoleCapture();
        // A failing hosted-login callback exercises the sensitive request
        // surfaces — query correlation material and the pending transaction.
        // Everything logged goes through the Serilog console while this test
        // holds Console.Out; the framework categories run at the image's own
        // Warning overrides, which the equivalence tests pin separately.
        await using var factory = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["AdminAuthentication:Provider"] = "OidcCode",
                ["IdentityService:Authority"] = "https://issuer.test",
                ["IdentityService:Issuer"] = "https://issuer.test",
                ["IdentityService:Audience"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientId"] = "client-id",
                ["AdminAuthentication:OidcCode:ClientSecret"] = "synthetic-hosted-secret-marker",
                ["AdminAuthentication:OidcCode:RedirectUri"] = "https://lexarbor.test/admin/auth/callback?registered=1",
                ["AdminAuthentication:OidcCode:Scope"] = "openid profile",
                ["RateLimits:AdminLogin:Enabled"] = "false"
            });
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var callback = await client.GetAsync(
            "/admin/auth/callback?registered=1&state=sensitive-state-marker&iss=https%3A%2F%2Fissuer.test&code=sensitive-code-marker",
            Ct);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var output = capture.Output;
        // Identity fields travel with every event the console emits.
        Assert.Contains("ServiceName", output);
        Assert.Contains("lexarbor", output);
        Assert.Contains("ServiceVersion", output);
        Assert.Contains("InstanceId", output);
        // The correlation material, the client secret and the pending-transaction
        // material never appear.
        Assert.DoesNotContain("sensitive-state-marker", output);
        Assert.DoesNotContain("sensitive-code-marker", output);
        Assert.DoesNotContain("synthetic-hosted-secret-marker", output);
        Assert.DoesNotContain("registered=1", output);
    }

    [Fact]
    public async Task SessionOperations_SuppressEntityFrameworkSqlUnderSerilog()
    {
        using var capture = new ConsoleCapture();
        await using var host = new VocabularyWebApplicationFactory("Testing", true);
        using var client = host.CreateClient();
        string handle;
        using (var scope = host.Services.CreateScope())
        {
            handle = await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(new()
            {
                AccessToken = "synthetic-console-access-marker",
                Issuer = VocabularyWebApplicationFactory.Issuer,
                Subject = "console-subject",
                DisplayName = "console-user",
                Roles = ["admin"],
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            }, Ct);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        request.Headers.Add("Cookie", $"{AdminSessionCookie.Name}={handle}");
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var output = capture.Output;
        Assert.DoesNotContain("admin_session", output);
        Assert.DoesNotContain(handle, output);
        Assert.DoesNotContain("synthetic-console-access-marker", output);
    }

    [Fact]
    public async Task CategoryLevels_MatchThePreMigrationConfiguration()
    {
        using var capture = new ConsoleCapture();
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();

        // Any request drives framework logging through the mapped categories.
        using var response = await client.GetAsync("/health", Ct);
        Assert.True(response.IsSuccessStatusCode);
        var output = capture.Output;
        // The startup identity lines are present at Information…
        Assert.Contains("Lexarbor starting, version", output);
        // …the image's Warning override for Microsoft.AspNetCore keeps
        // Hosting.Diagnostics Information out of the console…
        Assert.DoesNotContain("Request starting HTTP/1.1", output);
        // …and the Data Protection category never reaches any level.
        Assert.DoesNotContain("Microsoft.AspNetCore.DataProtection", output);
    }

    [Fact]
    public void LoggingLevels_MapExactlyOntoTheSerilogPipeline()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Warning",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Error",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "None"
            })
            .Build();
        var mapped = Lexarbor.Host.LexarborLoggingSetup.Map(configuration.GetSection("Logging:LogLevel"));

        Assert.Equal(LogLevel.Warning, mapped.MinimumLevel);
        var overridePair = Assert.Single(mapped.Overrides);
        Assert.Equal("Microsoft.AspNetCore", overridePair.Key);
        Assert.Equal(LogLevel.Error, overridePair.Value);
        var noneCategory = Assert.Single(mapped.NoneLevelCategories);
        Assert.Equal("Microsoft.EntityFrameworkCore.Database.Command", noneCategory);
    }

    [Fact]
    public void DefaultLevels_AloneLeaveNoOverrides()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information"
            })
            .Build();
        var mapped = Lexarbor.Host.LexarborLoggingSetup.Map(configuration.GetSection("Logging:LogLevel"));

        Assert.Equal(LogLevel.Information, mapped.MinimumLevel);
        Assert.Empty(mapped.Overrides);
        Assert.Empty(mapped.NoneLevelCategories);
    }

    [Theory]
    [InlineData("NotALevel")]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidLogLevel_FailsWithoutEchoingTheValue(string value)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Microsoft.AspNetCore"] = value
            })
            .Build();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            Lexarbor.Host.LexarborLoggingSetup.Map(configuration.GetSection("Logging:LogLevel")));

        Assert.Contains("Logging:LogLevel", failure.Message);
        if (value.Trim().Length > 0)
        {
            Assert.DoesNotContain(value, failure.Message);
        }
    }

    [Fact]
    public async Task SensitiveHeaders_ProjectDeniedValuesAsRedacted()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<SensitiveHeaderRegistry>();
        var projector = factory.Services.GetRequiredService<RequestHeaderDiagnosticProjector>();

        // Lexarbor adds no names of its own; the built-in authentication,
        // cookie and API-key headers are always denied.
        foreach (var name in new[]
                 {
                     "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "X-Auth-Token"
                 })
        {
            Assert.True(registry.IsSensitive(name));
        }

        Assert.False(registry.IsSensitive("X-Requested-With"));

        var headers = new HeaderDictionary
        {
            ["Authorization"] = "Bearer synthetic-token",
            ["Cookie"] = "__Host-Lexarbor.AdminSession=synthetic-handle",
            ["X-Requested-With"] = "XMLHttpRequest"
        };
        var projected = projector.Project(headers);
        Assert.Equal("[REDACTED]", projected["Authorization"].ToString());
        Assert.Equal("[REDACTED]", projected["Cookie"].ToString());
        Assert.Equal("XMLHttpRequest", projected["X-Requested-With"].ToString());
    }
}
