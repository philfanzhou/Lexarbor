using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Host;

/// <summary>
/// Maps the standard <c>Logging:LogLevel</c> section onto the ServiceMantle
/// Serilog pipeline options: <c>Default</c> becomes the pipeline minimum level
/// and every other category becomes a minimum-level override. A category set to
/// <see cref="LogLevel.None"/> — which a Serilog override cannot express — is
/// returned separately so the caller keeps it as a <c>LoggerFilterOptions</c>
/// rule, which the Serilog provider honors as well.
/// </summary>
public static class LexarborLoggingSetup
{
    public sealed record MappedLevels(
        LogLevel MinimumLevel,
        IReadOnlyDictionary<string, LogLevel> Overrides,
        IReadOnlyList<string> NoneLevelCategories);

    public static MappedLevels Map(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        var minimumLevel = LogLevel.Information;
        var overrides = new Dictionary<string, LogLevel>(StringComparer.Ordinal);
        var noneCategories = new List<string>();
        foreach (var entry in section.GetChildren())
        {
            LogLevel? level = null;
            if (entry.Value is not null && Enum.TryParse<LogLevel>(entry.Value, ignoreCase: true, out var parsed))
            {
                level = parsed;
            }

            // The invalid value is deliberately not echoed: it is operator-
            // supplied text, and both the key and the value stay out of the
            // failure message.
            if (level is null || string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new InvalidOperationException(
                    "Logging:LogLevel contains a key or value that is not a defined log level. " +
                    "Use a level such as Information, Warning or None.");
            }

            if (string.Equals(entry.Key, "Default", StringComparison.OrdinalIgnoreCase))
            {
                minimumLevel = level.Value;
            }
            else if (level.Value == LogLevel.None)
            {
                noneCategories.Add(entry.Key.Trim());
            }
            else
            {
                overrides[entry.Key.Trim()] = level.Value;
            }
        }

        return new MappedLevels(minimumLevel, overrides, noneCategories);
    }

    /// <summary>
    /// Hosting diagnostics run before application middleware and log the full
    /// request — query strings included — at Information, and the redirect and
    /// HTTP-logging categories do the same for the hosted-login round trips.
    /// These categories must remain off even with provider-specific Trace
    /// rules, so the correlation material of a login or logout never reaches a
    /// log sink.
    /// </summary>
    public static bool SuppressLogCategory(string? category) => category is "Microsoft.AspNetCore.Hosting.Diagnostics"
        or "Microsoft.AspNetCore.Http.Result.RedirectResult"
        || category?.StartsWith("Microsoft.AspNetCore.HttpLogging", StringComparison.Ordinal) == true;
}
