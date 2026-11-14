namespace Lexarbor.Host.RateLimiting;

/// <summary>
/// Optional administrator sign-in ceiling, partitioned by client address.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>
    /// <c>GET /admin/auth/start</c> initiates hosted sign-in. The default
    /// ceiling, when explicitly enabled, protects the identity provider while
    /// allowing ordinary login retries.
    /// </summary>
    public RateLimitPolicyOptions AdminLogin { get; set; } = new()
    {
        PermitLimit = 10,
        WindowSeconds = 300
    };
}

/// <summary>
/// One shared sliding window, divided into six segments. Enabled policies
/// accept 1..10000 permits and 10..600 seconds; invalid values fail startup.
/// </summary>
public sealed class RateLimitPolicyOptions
{
    /// <summary>
    /// Disabled by default. Set to true to apply the configured ceiling; setting
    /// false removes it without changing its numeric settings. Startup logs
    /// a warning naming each disabled policy.
    /// </summary>
    public bool Enabled { get; set; }

    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; }
}
