using Lexarbor.Domain.Models;

namespace Lexarbor.Database.Entities;

public sealed class EnrichmentTargetEntity
{
    public string Id { get; set; } = "";
    public string WordId { get; set; } = "";
    public string? MeaningId { get; set; }
    public AudioLocale Locale { get; set; }
    public EnrichmentStep Step { get; set; }
    public int CurrentRound { get; set; }
    public long Epoch { get; set; }
    public bool Deleted { get; set; }
}

public sealed class EnrichmentRoundEntity
{
    public string TargetId { get; set; } = "";
    public int Number { get; set; }
    public EnrichmentState State { get; set; }
    public string InputRevision { get; set; } = "";
    public string SourceRevision { get; set; } = "";
    public string? OwnAssetId { get; set; }
    public string ConfigurationVersion { get; set; } = "";
    public string? RetryKey { get; set; }
    public int FailureBudget { get; set; }
    public int RequestCount { get; set; }
    public int ConsumedFailures { get; set; }
    public string? PlanJson { get; set; }
    public EnrichmentWaitReason? WaitReason { get; set; }
    public string? ErrorCode { get; set; }
    public long? NextAttemptAtUnixMs { get; set; }
    public string? LeaseOwner { get; set; }
    public long LeaseFence { get; set; }
    public long? LeaseExpiresAtUnixMs { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}

public sealed class EnrichmentCallEntity
{
    public string Id { get; set; } = "";
    public string TargetId { get; set; } = "";
    public int Round { get; set; }
    public string Service { get; set; } = "";
    public EnrichmentCallResult Result { get; set; }
    public string? UploadIdempotencyKey { get; set; }
    public string? ErrorCode { get; set; }
    public long StartedAtUnixMs { get; set; }
    public long? FinishedAtUnixMs { get; set; }
}

public sealed class EnrichmentWaitEntity
{
    public string Id { get; set; } = "";
    public string TargetId { get; set; } = "";
    public int Round { get; set; }
    public EnrichmentWaitReason Reason { get; set; }
    public string? ErrorCode { get; set; }
    public long RecordedAtUnixMs { get; set; }
    public long NextAttemptAtUnixMs { get; set; }
}
