namespace Lexarbor.Database.Entities;

public sealed class AiRequestBatchEntity
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string ConfigurationVersion { get; set; } = "";
    public string Outcome { get; set; } = "Started";
    public int NextSize { get; set; }
    public long StartedAtUnixMs { get; set; }
    public long? FinishedAtUnixMs { get; set; }
}

public sealed class AiRequestBatchMemberEntity
{
    public string BatchId { get; set; } = "";
    // Enrichment call ID or review attempt ID, never a provider-supplied identity.
    public string Id { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Outcome { get; set; } = "Started";
}

public sealed class EnrichmentServiceLeaseEntity
{
    public string Id { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public string ConfigurationVersion { get; set; } = "";
    public long ExpiresAtUnixMs { get; set; }
}
