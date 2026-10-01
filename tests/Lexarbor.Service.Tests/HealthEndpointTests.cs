using System.Data.Common;
using System.Net;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The ServiceMantle health endpoints: readiness projects the startup migration
/// result plus one bounded read-only database probe, liveness answers status
/// alone, and both are anonymous and unmetered. Nothing a response carries may
/// disclose paths, connection strings or exception text.
/// </summary>
public class HealthEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HealthyHost_ReportsReadyOnBothReadinessRoutes()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/health", "/health/ready" })
        {
            using var response = await client.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("completed", body.RootElement.GetProperty("phase").GetString());
            Assert.Equal("succeeded", body.RootElement.GetProperty("migrationStatus").GetString());
            Assert.Equal("reachable", body.RootElement.GetProperty("databaseStatus").GetString());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("errorCode").ValueKind);
        }
    }

    [Fact]
    public async Task Liveness_AnswersLiveIndependentlyOfReadiness()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/live", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("live", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(["status"], body.RootElement.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task FailingDatabaseProbe_Answers503WithFixedErrorCode()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddDbContext<VocabularyDbContext>(options => options
                    .AddInterceptors(new FailingProbeInterceptor()));
            });
        });
        using var client = configured.CreateClient();

        using var response = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("synthetic-storage-secret", raw);
        using var body = JsonDocument.Parse(raw);
        Assert.Equal("not_ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("unreachable", body.RootElement.GetProperty("databaseStatus").GetString());
        Assert.Equal("vocabulary.database_unreachable", body.RootElement.GetProperty("errorCode").GetString());
        // Liveness is untouched by the database.
        using var live = await client.GetAsync("/health/live", Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task DisabledInitializationWithPendingMigrations_IsNotReady()
    {
        await using var factory = new VocabularyWebApplicationFactory(
            environment: "Testing",
            includeAppCredentials: true,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Database:InitializeOnStartup"] = "false"
            });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("not_ready", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("notStarted", body.RootElement.GetProperty("migrationStatus").GetString());
        Assert.Equal("reachable", body.RootElement.GetProperty("databaseStatus").GetString());
    }

    [Fact]
    public async Task HealthRoutes_AreAnonymousAndOutsideThePublicApiCeiling()
    {
        await using var factory = new VocabularyWebApplicationFactory(
            environment: "Testing",
            includeAppCredentials: true,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["RateLimits:PublicApi:Enabled"] = "true",
                ["RateLimits:PublicApi:PermitLimit"] = "1",
                ["RateLimits:PublicApi:WindowSeconds"] = "300"
            });
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// Exposes the factory's shared in-memory connection so a re-registered
    /// DbContext keeps addressing the same database.
    /// </summary>
    private sealed class FailingProbeInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            // EF composes the raw probe SQL, so the exact stored text differs;
            // match the probe's distinctive alias instead of exact equality.
            // EF composes the raw probe SQL, so the stored text differs from the
            // literal; match the probe's distinctive alias.
            if (command.CommandText.Contains("1 AS", StringComparison.Ordinal))
            {
                throw new SqliteException("synthetic-storage-secret", 1);
            }

            return ValueTask.FromResult(result);
        }
    }
}
