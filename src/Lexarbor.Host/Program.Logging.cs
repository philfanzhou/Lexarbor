using Lexarbor.Host;

public partial class Program
{
    /// <summary>
    /// Console logging runs through the ServiceMantle Serilog pipeline: every
    /// structured property is sanitized before it reaches the console, and the
    /// service identity travels with each event. The `Logging:LogLevel` section is
    /// mapped onto the pipeline: `Default` becomes the minimum level, every other
    /// category becomes a minimum-level override, and `None` — which an override
    /// cannot express — keeps its LoggerFilterOptions rule below.
    /// </summary>
    private static void ConfigureLogging(WebApplicationBuilder builder)
    {
        var mappedLogLevels = LoggingSetup.Map(builder.Configuration.GetSection("Logging:LogLevel"));
        builder.AddServiceMantleSerilog(options =>
        {
            options.MinimumLevel = mappedLogLevels.MinimumLevel;
            options.MinimumLevelOverrides = mappedLogLevels.Overrides.Count > 0
                ? new Dictionary<string, LogLevel>(mappedLogLevels.Overrides, StringComparer.Ordinal)
                : null;
        });
        foreach (var category in mappedLogLevels.NoneLevelCategories)
        {
            builder.Logging.AddFilter(category, LogLevel.None);
        }
    }
}
