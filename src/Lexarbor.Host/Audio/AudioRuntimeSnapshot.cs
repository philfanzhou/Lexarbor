namespace Lexarbor.Host.Audio;

/// <summary>A complete, immutable, credential-free operation configuration.</summary>
public sealed record AudioRuntimeSnapshot
{
    public long Generation { get; init; }
    public bool Enabled { get; init; }
    public string InternalEndpoint { get; init; } = "";
    public string PublicEndpoint { get; init; } = "";
    public string Region { get; init; } = "";
    public string Bucket { get; init; } = "";
    public string KeyPrefix { get; init; } = "";
    public int PlaybackTtlSeconds { get; init; }
    public long MaxUploadBytes { get; init; }
    public int MaxConcurrentUploads { get; init; }
    public int MaxQueuedUploads { get; init; }
    public int OperationTimeoutSeconds { get; init; }
    public int RecoveryBatchSize { get; init; }
    public int RecoveryIntervalSeconds { get; init; }
    public int RecoveryLeaseSeconds { get; init; }
    public int RecoveryRetrySeconds { get; init; }
    public int RecoveryMaxRetrySeconds { get; init; }
    public int RecoveryRecheckSeconds { get; init; }

    internal static AudioRuntimeSnapshot From(AudioOptions options, long generation) => new()
    {
        Generation = generation,
        Enabled = options.Enabled,
        InternalEndpoint = options.InternalEndpoint,
        PublicEndpoint = options.PublicEndpoint,
        Region = options.Region,
        Bucket = options.Bucket,
        KeyPrefix = options.KeyPrefix,
        PlaybackTtlSeconds = options.PlaybackTtlSeconds,
        MaxUploadBytes = options.MaxUploadBytes,
        MaxConcurrentUploads = options.MaxConcurrentUploads,
        MaxQueuedUploads = options.MaxQueuedUploads,
        OperationTimeoutSeconds = options.OperationTimeoutSeconds,
        RecoveryBatchSize = options.RecoveryBatchSize,
        RecoveryIntervalSeconds = options.RecoveryIntervalSeconds,
        RecoveryLeaseSeconds = options.RecoveryLeaseSeconds,
        RecoveryRetrySeconds = options.RecoveryRetrySeconds,
        RecoveryMaxRetrySeconds = options.RecoveryMaxRetrySeconds,
        RecoveryRecheckSeconds = options.RecoveryRecheckSeconds,
    };
}
