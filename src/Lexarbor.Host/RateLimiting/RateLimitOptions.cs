namespace Lexarbor.Host.RateLimiting;

/// <summary>
/// Request ceilings for the two anonymous surfaces. Both are per client address
/// rather than global: a global ceiling on an anonymous endpoint is itself a
/// denial-of-service tool, because one caller can spend the whole budget and
/// lock everyone else out, including the administrator trying to log in.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>
    /// <c>GET /admin/auth/start</c> initiates hosted sign-in. The default
    /// protects the identity provider while allowing ordinary login retries.
    /// </summary>
    public RateLimitPolicyOptions AdminLogin { get; set; } = new()
    {
        PermitLimit = 10,
        WindowSeconds = 300
    };

    /// <summary>
    /// The anonymous <c>/api/*</c> routes. Deliberately loose: these are meant
    /// to be called by an application, and the ceiling exists to stop one
    /// address from monopolising a single-instance SQLite deployment, not to
    /// meter normal use.
    /// </summary>
    public RateLimitPolicyOptions PublicApi { get; set; } = new()
    {
        PermitLimit = 300,
        WindowSeconds = 60
    };
}

/// <summary>
/// One shared sliding window, divided into six segments. Enabled policies
/// accept 1..10000 permits and 10..600 seconds; invalid values fail startup.
/// </summary>
public sealed class RateLimitPolicyOptions
{
    /// <summary>
    /// Set to false to remove the ceiling. Present so that turning a limit off is
    /// a deliberate, greppable configuration value rather than something an
    /// operator achieves by setting the permit count absurdly high; startup logs
    /// a warning naming the policy, so a disabled limit cannot be a quiet state.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; }
}
