using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Services;

/// <summary>
/// The rules the audio recovery loops share: which object keys they may touch and when a
/// failed cleanup is retried.
/// </summary>
internal static class AudioRecoveryPolicy
{
    /// <summary>
    /// True for a key under the configured prefix that names exactly one generated id: 32
    /// lower-case hex characters and the <c>.mp3</c> extension.
    /// </summary>
    internal static bool IsControlled(AudioObjectLocation location, AudioRecoverySettings settings)
    {
        if (location.Bucket != settings.Bucket || !location.Key.StartsWith(settings.KeyPrefix, StringComparison.Ordinal)) return false;
        var suffix = location.Key[settings.KeyPrefix.Length..];
        return suffix.Length == 36 && suffix.EndsWith(".mp3", StringComparison.Ordinal)
            && Guid.TryParseExact(suffix[..32], "N", out _) && suffix[..32] == suffix[..32].ToLowerInvariant();
    }

    /// <summary>The next retry time: the base delay doubled per earlier attempt, capped at the configured maximum.</summary>
    internal static long RetryAt(int attempts, AudioRecoverySettings settings, long now) => now + Math.Min(settings.MaxRetrySeconds,
        settings.RetrySeconds * (1L << Math.Clamp(attempts - 1, 0, 20))) * 1000;
}
