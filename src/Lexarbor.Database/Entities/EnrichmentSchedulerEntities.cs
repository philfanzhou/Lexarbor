namespace Lexarbor.Database.Entities;

public sealed class EnrichmentDiscoveryEntity
{
    public string Id { get; set; } = "";
    public string Cursor { get; set; } = "";
    public string UpperBound { get; set; } = "";
}

public sealed class EnrichmentServiceEntity
{
    public string Id { get; set; } = "";
    public string ConfigurationVersion { get; set; } = "";
    public int ConsecutiveFailures { get; set; }
    public int ThrottleSeconds { get; set; }
    public bool Paused { get; set; }
    public bool Blocked { get; set; }
    public int PauseSeconds { get; set; }
    public long NextAttemptAtUnixMs { get; set; }
    public string? LeaseId { get; set; }
    public long? LeaseExpiresAtUnixMs { get; set; }
}

public sealed class EnrichmentServiceProbeEntity
{
    public string Id { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public string Result { get; set; } = "Started";
    public bool RequestMade { get; set; }
    public long StartedAtUnixMs { get; set; }
    public long? FinishedAtUnixMs { get; set; }
}
