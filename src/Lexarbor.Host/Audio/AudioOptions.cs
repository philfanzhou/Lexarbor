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

    public bool IsValid() => !Enabled ||
        (ValidEndpoint(InternalEndpoint, false) && ValidEndpoint(PublicEndpoint, true)
        && !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey)
        && Regex.IsMatch(Region, "^[a-z0-9-]{1,63}$", RegexOptions.CultureInvariant)
        && Regex.IsMatch(Bucket, "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)
        && Regex.IsMatch(KeyPrefix, "^(?:[a-z0-9][a-z0-9-]{0,31}/){1,4}$", RegexOptions.CultureInvariant)
        && PlaybackTtlSeconds is >= 60 and <= 3600
        && MaxUploadBytes is >= 1 and <= 20 * 1024 * 1024
        && MaxConcurrentUploads is >= 1 and <= 8 && MaxQueuedUploads is >= 0 and <= 8
        && OperationTimeoutSeconds is >= 1 and <= 120);

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
        return services;
    }
}
