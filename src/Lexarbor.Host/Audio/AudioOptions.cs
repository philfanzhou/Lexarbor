using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.Audio;

public sealed class AudioOptions
{
    public const string SectionName = "Audio";
    public const string ConfigurationFailure = "Audio configuration is invalid. Check endpoints, credentials, bucket, key prefix and limits.";
    public bool Enabled { get; set; }
    public string InternalEndpoint { get; set; } = "";
    public string PublicEndpoint { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "lexarbor-audio";
    public string KeyPrefix { get; set; } = "audio/";
    public int PlaybackTtlSeconds { get; set; } = 900;
    public long MaxUploadBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxConcurrentUploads { get; set; } = 2;
    public int MaxQueuedUploads { get; set; } = 8;
    public int OperationTimeoutSeconds { get; set; } = 60;

    public int RecoveryBatchSize { get; set; } = 20;
    public int RecoveryIntervalSeconds { get; set; } = 60;
    public int RecoveryLeaseSeconds { get; set; } = 300;
    public int RecoveryRetrySeconds { get; set; } = 60;
    public int RecoveryMaxRetrySeconds { get; set; } = 3600;
    public int RecoveryRecheckSeconds { get; set; } = 86400;

    internal AudioOptions Copy() => new()
    {
        Enabled = Enabled,
        InternalEndpoint = InternalEndpoint,
        PublicEndpoint = PublicEndpoint,
        AccessKey = AccessKey,
        SecretKey = SecretKey,
        Region = Region,
        Bucket = Bucket,
        KeyPrefix = KeyPrefix,
        PlaybackTtlSeconds = PlaybackTtlSeconds,
        MaxUploadBytes = MaxUploadBytes,
        MaxConcurrentUploads = MaxConcurrentUploads,
        MaxQueuedUploads = MaxQueuedUploads,
        OperationTimeoutSeconds = OperationTimeoutSeconds,
        RecoveryBatchSize = RecoveryBatchSize,
        RecoveryIntervalSeconds = RecoveryIntervalSeconds,
        RecoveryLeaseSeconds = RecoveryLeaseSeconds,
        RecoveryRetrySeconds = RecoveryRetrySeconds,
        RecoveryMaxRetrySeconds = RecoveryMaxRetrySeconds,
        RecoveryRecheckSeconds = RecoveryRecheckSeconds,
    };

    public bool IsValid() => !Enabled ||
        (ValidEndpoint(InternalEndpoint, false) && ValidEndpoint(PublicEndpoint, true)
        && !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey)
        && Regex.IsMatch(Region, "^[a-z0-9-]{1,63}$", RegexOptions.CultureInvariant)
        && Regex.IsMatch(Bucket, "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)
        && Regex.IsMatch(KeyPrefix, "^(?:[a-z0-9][a-z0-9-]{0,31}/){1,4}$", RegexOptions.CultureInvariant)
        && PlaybackTtlSeconds is >= 60 and <= 3600
        && MaxUploadBytes is >= 1 and <= 20 * 1024 * 1024
        && MaxConcurrentUploads is >= 1 and <= 8 && MaxQueuedUploads is >= 0 and <= 8
        && OperationTimeoutSeconds is >= 1 and <= 120
        && RecoveryBatchSize is >= 1 and <= 100 && RecoveryIntervalSeconds is >= 10 and <= 3600
        && RecoveryLeaseSeconds is >= 30 and <= 900 && RecoveryLeaseSeconds > OperationTimeoutSeconds
        && RecoveryRetrySeconds is >= 10 and <= 3600
        && RecoveryMaxRetrySeconds >= RecoveryRetrySeconds && RecoveryMaxRetrySeconds <= 86400
        && RecoveryRecheckSeconds is >= 3600 and <= 604800);

    private static bool ValidEndpoint(string value, bool allowPrefix) =>
        !value.Contains("..", StringComparison.Ordinal)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host)
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && (allowPrefix ? Regex.IsMatch(uri.AbsolutePath, "^/(?:[a-zA-Z0-9_-]+/)*$", RegexOptions.CultureInvariant)
            : uri.AbsolutePath == "/");
}

public static class AudioServiceCollectionExtensions
{
    public static IServiceCollection AddAudioStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AudioOptions>()
            .Configure(options =>
            {
                try
                {
                    configuration.GetSection(AudioOptions.SectionName).Bind(options);
                }
                catch (InvalidOperationException)
                {
                    // Binder conversion errors include the supplied value, including in
                    // their inner exception. Replace them before startup can log them.
                    throw new OptionsValidationException(Options.DefaultName, typeof(AudioOptions),
                        [AudioOptions.ConfigurationFailure]);
                }
            })
            .Validate(options => options.IsValid(), AudioOptions.ConfigurationFailure)
            .ValidateOnStart();
        services.AddSingleton<Lexarbor.Domain.Repositories.IAudioObjectStore, S3AudioObjectStore>();
        // Internal only until every audio entry point uses an operation reference.
        services.AddSingleton<IAudioObjectStoreFactory, S3AudioObjectStoreFactory>();
        services.AddSingleton<AudioRuntimeConfiguration>();
        return services;
    }
}
