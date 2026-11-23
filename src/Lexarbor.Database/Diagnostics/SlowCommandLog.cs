using System.Data.Common;
using Lexarbor.Database.Repositories;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Database.Diagnostics;

/// <summary>
/// Writes one warning for each database command that takes at least the threshold, so a slow
/// request can be traced to its statement. The entry carries the parameterized SQL text and never
/// parameter values; commands that run for an administrator session are not logged at all.
/// </summary>
public sealed class SlowCommandLog(TimeSpan threshold, ILogger<SlowCommandLog> logger) : DbCommandInterceptor
{
    public const string ThresholdKey = "Database:SlowCommandThresholdMilliseconds";
    public const int DefaultThresholdMilliseconds = 500;
    internal const int MaxCommandTextLength = 4000;

    private static readonly Action<ILogger, string, long, long, string, string, string, Exception?> Entry =
        LoggerMessage.Define<string, long, long, string, string, string>(
            LogLevel.Warning,
            new EventId(1, "SlowDatabaseCommand"),
            "Database command {Outcome} after {ElapsedMilliseconds} ms (threshold {ThresholdMilliseconds} ms, {Context}.{Method}): {CommandText}");

    public TimeSpan Threshold { get; } = threshold;

    /// <summary>
    /// Reads the threshold from configuration: absent means the default, zero turns the log off,
    /// and anything else that is not a positive whole number of milliseconds stops startup.
    /// </summary>
    public static TimeSpan? ReadThreshold(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return TimeSpan.FromMilliseconds(DefaultThresholdMilliseconds);
        if (!int.TryParse(configured, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
            throw new InvalidOperationException($"{ThresholdKey} must be a whole number of milliseconds, 0 to turn the slow command log off.");
        return milliseconds == 0 ? null : TimeSpan.FromMilliseconds(milliseconds);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Record(command, eventData, "completed");
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        Record(command, eventData, "completed");
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Record(command, eventData, "completed");
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        Record(command, eventData, "completed");
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Record(command, eventData, "completed");
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        object? result, CancellationToken cancellationToken = default)
    {
        Record(command, eventData, "completed");
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        Record(command, eventData, "failed");

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, "failed");
        return Task.CompletedTask;
    }

    // A caller that gives up on a slow command, such as a request whose client disconnected,
    // cancels it; that is the slow case this log exists for.
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) =>
        Record(command, eventData, "canceled");

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(command, eventData, "canceled");
        return Task.CompletedTask;
    }

    private void Record(DbCommand command, CommandEndEventData eventData, string outcome)
    {
        if (eventData.Duration < Threshold || AdminSessionRepository.IsSessionOperation || !logger.IsEnabled(LogLevel.Warning))
            return;
        var text = command.CommandText;
        if (text.Length > MaxCommandTextLength) text = string.Concat(text.AsSpan(0, MaxCommandTextLength), " [truncated]");
        Entry(logger, outcome, (long)eventData.Duration.TotalMilliseconds, (long)Threshold.TotalMilliseconds,
            eventData.Context?.GetType().Name ?? "DbContext", eventData.ExecuteMethod.ToString(), text, null);
    }
}
