using System.Globalization;
using System.Threading.RateLimiting;
using Lexarbor.Service;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host.RateLimiting;

public static class RateLimitingExtensions
{
    public const string AdminLoginPolicy = "admin-login";
    public const string PublicApiPolicy = "public-api";

    /// <summary>
    /// Shared by both policies so a rejected caller cannot tell which ceiling it
    /// hit, and so the body stays in the envelope every other failure uses.
    /// </summary>
    private const string RejectionMessage = "Too many requests. Please retry later.";

    public static void AddLexarborRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            // Refused at startup rather than clamped, and validated here rather
            // than while registering, because configuration a test host or a
            // late-added provider supplies is not composed yet at registration
            // time. Reading it there would validate the image defaults and let
            // the value that actually takes effect go unchecked.
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();

        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(AdminLoginPolicy, context =>
                Partition(context, Current(context).AdminLogin));
            limiter.AddPolicy(PublicApiPolicy, context =>
                Partition(context, Current(context).PublicApi));

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                if (response.HasStarted)
                {
                    return;
                }

                // Present for a fixed window, and the only thing that makes a 429
                // actionable: without it a well-behaved client has to guess, and
                // guessing short is indistinguishable from not backing off.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                }

                await VocabularyHttpResponse.WriteFailureAsync(
                    response,
                    StatusCodes.Status429TooManyRequests,
                    RejectionMessage);
            };
        });
    }

    private static RateLimitOptions Current(HttpContext context)
    {
        return context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
    }

    private static RateLimitPartition<string> Partition(
        HttpContext context,
        RateLimitPolicyOptions policy)
    {
        if (!policy.Enabled)
        {
            return RateLimitPartition.GetNoLimiter(ClientKey(context));
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = TimeSpan.FromSeconds(policy.WindowSeconds),
                // No queue. Holding an over-limit request open consumes exactly
                // the server resource the ceiling exists to protect, and a caller
                // learns nothing from a slow 200 that it would not learn faster
                // from an immediate 429 carrying Retry-After.
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    /// <summary>
    /// The partition key. A request with no remote address shares one bucket with
    /// every other such request rather than receiving its own, so being
    /// unidentifiable cannot be the cheapest way past the ceiling.
    /// </summary>
    private static string ClientKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address == null)
        {
            return "unknown";
        }

        // A dual-stack socket reports an IPv4 client as ::ffff:203.0.113.9. Left
        // alone, the same client would hold two partitions depending on how it
        // happened to connect.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }

    private sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
    {
        public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
        {
            var failures = new List<string>();
            Check(nameof(options.AdminLogin), options.AdminLogin, failures);
            Check(nameof(options.PublicApi), options.PublicApi, failures);
            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        private static void Check(
            string name,
            RateLimitPolicyOptions policy,
            List<string> failures)
        {
            if (!policy.Enabled)
            {
                return;
            }

            // A permit count of zero would reject every request and a negative
            // window would throw somewhere far from its cause. Both are typos,
            // and a service that silently repairs a typo in a security ceiling
            // teaches the operator that the value they wrote took effect.
            if (policy.PermitLimit < 1 || policy.WindowSeconds < 1)
            {
                failures.Add(
                    $"RateLimits:{name} must have a PermitLimit and WindowSeconds of at " +
                    $"least 1, or Enabled set to false. Found PermitLimit={policy.PermitLimit}, " +
                    $"WindowSeconds={policy.WindowSeconds}.");
            }
        }
    }

}
