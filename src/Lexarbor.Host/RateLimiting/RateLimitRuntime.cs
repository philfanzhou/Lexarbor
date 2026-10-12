using Microsoft.Extensions.Options;

namespace Lexarbor.Host.RateLimiting;

public sealed record RateLimitPolicySnapshot(bool Enabled, int PermitLimit, int WindowSeconds);
public sealed record RateLimitSnapshot(RateLimitPolicySnapshot AdminLogin, RateLimitPolicySnapshot PublicApi)
{
    public static RateLimitSnapshot From(RateLimitOptions options) => new(
        new(options.AdminLogin.Enabled, options.AdminLogin.PermitLimit, options.AdminLogin.WindowSeconds),
        new(options.PublicApi.Enabled, options.PublicApi.PermitLimit, options.PublicApi.WindowSeconds));
}

public sealed class RateLimitRuntime(IOptions<RateLimitOptions> options)
{
    private RateLimitSnapshot _current = RateLimitSnapshot.From(options.Value);
    public RateLimitSnapshot Current => Volatile.Read(ref _current);
    public void Publish(RateLimitSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
}
