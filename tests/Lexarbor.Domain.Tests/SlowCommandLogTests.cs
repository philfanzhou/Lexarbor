using Lexarbor.Database.Diagnostics;
using Lexarbor.Database.Repositories;
using Lexarbor.Learning.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// A command at or above the threshold leaves one warning with its parameterized SQL, so a slow
/// request can be traced to its statement without parameter values or administrator sessions in the log.
/// </summary>
public sealed class SlowCommandLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASlowCommandIsLoggedWithoutItsParameterValues()
    {
        var log = new CapturingLogger();
        using var file = new AudioTestDatabase();
        await using var db = file.Open(new SlowCommandLog(TimeSpan.FromMilliseconds(20), log));
        const string marker = "learner-secret-7f3a";

        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_sleep(0.05), CAST({marker} AS text)", Ct);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("SlowDatabaseCommand", entry.Event.Name);
        Assert.Contains("completed after", entry.Message);
        Assert.Contains("threshold 20 ms, VocabularyDbContext.ExecuteNonQuery", entry.Message);
        Assert.Contains("SELECT pg_sleep(0.05), CAST(@p0 AS text)", entry.Message);
        Assert.DoesNotContain(marker, entry.Message);
        Assert.True(entry.Elapsed >= 50, entry.Message);
    }

    [Fact]
    public async Task AFastCommandIsNotLogged()
    {
        var log = new CapturingLogger();
        using var file = new AudioTestDatabase();
        await using var db = file.Open(new SlowCommandLog(TimeSpan.FromSeconds(10), log));

        await db.Vocabularies.CountAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("SELECT 1", Ct);

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task ASlowFailureIsLoggedAsFailed()
    {
        var log = new CapturingLogger();
        using var file = new AudioTestDatabase();
        await using var db = file.Open(new SlowCommandLog(TimeSpan.FromMilliseconds(20), log));

        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(0.05); SELECT 1 / 0", Ct));

        var entry = Assert.Single(log.Entries);
        Assert.Contains("failed after", entry.Message);
        Assert.Contains("SELECT 1 / 0", entry.Message);
    }

    [Fact]
    public async Task ACanceledSlowCommandIsLoggedAsCanceled()
    {
        var log = new CapturingLogger();
        using var file = new AudioTestDatabase();
        await using var db = file.Open(new SlowCommandLog(TimeSpan.FromMilliseconds(20), log));
        // The clock starts once the connection is open, so the command itself runs long enough.
        await db.Database.OpenConnectionAsync(Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Database.ExecuteSqlRawAsync("SELECT pg_sleep(5)", cancel.Token));

        var entry = Assert.Single(log.Entries);
        Assert.Contains("canceled after", entry.Message);
        Assert.True(entry.Elapsed >= 20, entry.Message);
    }

    [Fact]
    public async Task ALongCommandTextIsTruncated()
    {
        var log = new CapturingLogger();
        using var file = new AudioTestDatabase();
        await using var db = file.Open(new SlowCommandLog(TimeSpan.FromMilliseconds(20), log));

        var longCommand = $"SELECT pg_sleep(0.05) /* {new string('x', 2 * SlowCommandLog.MaxCommandTextLength)} */";
        await db.Database.ExecuteSqlRawAsync(longCommand, Ct);

        var entry = Assert.Single(log.Entries);
        Assert.EndsWith("x [truncated]", entry.Message);
        Assert.True(entry.Message.Length < SlowCommandLog.MaxCommandTextLength + 200, $"{entry.Message.Length} characters");
    }

    [Fact]
    public async Task AdministratorSessionCommandsAreNeverLogged()
    {
        var log = new CapturingLogger();
        using var file = new AudioTestDatabase();
        await using var db = file.Open(new SlowCommandLog(TimeSpan.Zero, log));
        await db.Database.MigrateAsync(Ct);
        log.Entries.Clear();

        Assert.Null(await new AdminSessionRepository(db, new UnitOfWork(db)).ReadAsync(AudioTestDatabase.Hash, Ct));
        Assert.Empty(log.Entries);

        await db.AdminSessions.CountAsync(Ct);
        Assert.Single(log.Entries);
    }

    [Fact]
    public void LearnerContextsCarryTheLog()
    {
        var log = new SlowCommandLog(TimeSpan.FromMilliseconds(500), new CapturingLogger());

        var options = new LearningStorage("Host=localhost;Database=lexarbor", log).Options("caller");

        Assert.Contains(log, options.FindExtension<CoreOptionsExtension>()!.Interceptors!);
        Assert.Empty(new LearningStorage("Host=localhost;Database=lexarbor").Options("caller")
            .FindExtension<CoreOptionsExtension>()!.Interceptors ?? []);
    }

    [Theory]
    [InlineData(null, 500)]
    [InlineData("", 500)]
    [InlineData("250", 250)]
    [InlineData("0", null)]
    public void TheThresholdDefaultsToHalfASecondAndZeroTurnsItOff(string? configured, int? milliseconds)
    {
        Assert.Equal(milliseconds is { } value ? TimeSpan.FromMilliseconds(value) : null, SlowCommandLog.ReadThreshold(configured));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("500ms")]
    [InlineData("99999999999")]
    public void AnInvalidThresholdIsRefused(string configured)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SlowCommandLog.ReadThreshold(configured));

        Assert.Contains(SlowCommandLog.ThresholdKey, error.Message);
        Assert.DoesNotContain(configured, error.Message);
    }

    private sealed record Entry(LogLevel Level, EventId Event, string Message, long Elapsed);

    private sealed class CapturingLogger : ILogger<SlowCommandLog>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            var elapsed = values.FirstOrDefault(pair => pair.Key == "ElapsedMilliseconds").Value as long? ?? -1;
            lock (Entries) Entries.Add(new(logLevel, eventId, formatter(state, exception), elapsed));
        }
    }
}
