using System.Text.Json;
using Lexarbor.Learning.Domain.Calendar;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Lexarbor.Service.Tests;

/// <summary>Learning days follow UTC+08:00 unless the deployment configures VocabularyLearning:TimeZone.</summary>
public sealed class LearningCalendarTests : IAsyncDisposable
{
    // 02:00 UTC: already 10 July at UTC+8, still 9 July in New York (UTC-4 in summer).
    private static readonly DateTimeOffset SummerMorning = new(2026, 7, 10, 2, 0, 0, TimeSpan.Zero);
    private readonly List<IAsyncDisposable> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in Enumerable.Reverse(_hosts)) await host.DisposeAsync();
    }

    [Fact]
    public void TheDefaultCalendarSplitsDaysAtMidnightUtcPlusEight()
    {
        var calendar = LearningCalendar.Create(null);

        Assert.Same(LearningCalendar.Default, calendar);
        Assert.Equal(new DateOnly(2026, 1, 1), calendar.From(new DateTimeOffset(2026, 1, 1, 15, 59, 59, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 1, 2), calendar.From(new DateTimeOffset(2026, 1, 1, 16, 0, 0, TimeSpan.Zero)));
        Assert.Equal(480, calendar.OffsetMinutes(SummerMorning));
    }

    [Fact]
    public void AConfiguredZoneFollowsItsDaylightSavingOffset()
    {
        var calendar = LearningCalendar.Create(" America/New_York ");

        Assert.Equal(new DateOnly(2026, 7, 9), calendar.From(SummerMorning));
        Assert.Equal(-240, calendar.OffsetMinutes(SummerMorning));
        Assert.Equal(-300, calendar.OffsetMinutes(new DateTimeOffset(2026, 1, 10, 2, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Asia/Shanghai", true)]
    [InlineData("UTC", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("+08:00", false)]
    public void TheOptionAcceptsOnlyKnownZones(string? timeZone, bool valid)
    {
        var errors = VocabularyLearningOptions.Validate(new VocabularyLearningOptions { TimeZone = timeZone }).ToArray();

        Assert.Equal(valid, errors.Length == 0);
        if (!valid) Assert.Contains("VocabularyLearning:TimeZone", Assert.Single(errors));
    }

    [Theory]
    [InlineData(null, "2026-07-10", 480)]
    [InlineData("America/New_York", "2026-07-09", -240)]
    public async Task TheOverviewReportsTheDeploymentsLearningDay(string? timeZone, string localDate, int offset)
    {
        var client = await ClientAsync(timeZone);
        var student = Guid.NewGuid();
        (await client.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();

        var response = await client.GetAsync(
            $"/learning/v1/api/vocabulary/students/{student}/overview?localDate=2026-07-10&tzOffsetMinutes=480", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");

        Assert.Equal(localDate, data.GetProperty("localDate").GetString());
        Assert.Equal(offset, data.GetProperty("tzOffsetMinutes").GetInt32());
    }

    [Fact]
    public void AnUnknownZoneStopsStartup()
    {
        var factory = Host("Mars/Olympus_Mons");

        // The test host reports a failed start as its own disposal; the option validation
        // above pins the message that a real process logs.
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    private async Task<HttpClient> ClientAsync(string? timeZone)
    {
        var host = Host(timeZone);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("learning-calendar", catalog: false, learning: true));
        return client;
    }

    private WebApplicationFactory<Program> Host(string? timeZone)
    {
        var factory = new VocabularyWebApplicationFactory();
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["VocabularyLearning:TimeZone"] = timeZone }));
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FixedClock(SummerMorning)));
        });
        _hosts.Add(factory);
        _hosts.Add(host);
        return host;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
