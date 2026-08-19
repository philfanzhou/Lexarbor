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
    /// <c>POST /admin/auth/login</c>. Anonymous, and every call forwards a
    /// username and password to the identity provider, so an unlimited endpoint
    /// is both a password-guessing oracle and a way to aim traffic at the
    /// provider from an address the provider sees as Lexarbor's.
    ///
    /// The default allows an administrator to mistype a password several times
    /// in a row and still get in, while reducing an exhaustive search to a rate
    /// that would take longer than the credential's useful life.
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
/// One fixed window. Fixed rather than sliding because the reset is something an
/// operator reading <c>Retry-After</c> can reason about, and because the burst a
/// fixed window permits at a boundary is irrelevant at these limits.
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
