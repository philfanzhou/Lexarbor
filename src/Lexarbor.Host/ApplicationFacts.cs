namespace Lexarbor.Host;

/// <summary>
/// Properties of the host that the settings surface reports and no configuration
/// changes. Each is a decision made once, here and where it is applied.
/// </summary>
public static class ApplicationFacts
{
    /// <summary>The one HTTP port the host listens on, HTTP/1 only.</summary>
    public const int HttpPort = 5008;

    /// <summary>The ServiceMantle service identifier reported with every log event.</summary>
    public const string ServiceName = "lexarbor";

    /// <summary>The Data Protection application name that scopes the key ring.</summary>
    public const string DataProtectionApplicationName = "Lexarbor";

    /// <summary>Where the persistent configuration lives, relative to the content root.</summary>
    public const string ConfigurationFile = "data/appsettings.json";

    /// <summary>Where startup looks for a database left by a release before PostgreSQL.</summary>
    public const string LegacySqlitePath = "data/vocabulary.db";
}
