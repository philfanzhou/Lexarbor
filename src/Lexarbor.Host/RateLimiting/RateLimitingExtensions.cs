using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Lexarbor.Service;
// Microsoft.AspNetCore.HttpOverrides carries a deprecated IPNetwork of its own.
using IPNetwork = System.Net.IPNetwork;

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

    /// <summary>
    /// Configures forwarded-header handling for the case where an operator has
    /// named the hops to trust. Program.cs adds the middleware only then, because
    /// ForwardedHeadersMiddleware treats empty trust lists as "skip the origin
    /// check" rather than "trust nobody" and would otherwise apply the header
    /// from any caller.
    ///
    /// That default is the conservative one in both directions. Behind an
    /// unconfigured proxy the limiter sees only the proxy and degrades to a
    /// shared ceiling, which is a visible operational problem. Trusting
    /// <c>X-Forwarded-For</c> from anyone instead would let a caller mint a fresh
    /// partition key per request and pass the ceiling without reaching it, which
    /// is an invisible security one.
    /// </summary>
    public static void AddLexarborForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<NetworkOptions>()
            .Bind(configuration.GetSection(NetworkOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NetworkOptions>, NetworkOptionsValidator>();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<NetworkOptions>>((options, network) =>
            {
                var value = network.Value;
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = value.ForwardLimit;

                // The defaults trust loopback, which inside a container means
                // anything sharing the network namespace could rewrite its own
                // address. Only what the operator listed is trusted.
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
                foreach (var proxy in value.TrustedProxies)
                {
                    options.KnownProxies.Add(IPAddress.Parse(proxy));
                }

                foreach (var range in value.TrustedNetworks)
                {
                    options.KnownIPNetworks.Add(IPNetwork.Parse(range));
                }
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

    private sealed class NetworkOptionsValidator : IValidateOptions<NetworkOptions>
    {
        public ValidateOptionsResult Validate(string? name, NetworkOptions options)
        {
            var failures = new List<string>();
            foreach (var proxy in options.TrustedProxies)
            {
                if (!IPAddress.TryParse(proxy, out _))
                {
                    failures.Add(
                        $"Network:TrustedProxies contains '{proxy}', which is not an IP address.");
                }
            }

            foreach (var range in options.TrustedNetworks)
            {
                // Rejects a range whose address carries bits below the prefix, so
                // 172.18.0.1/16 does not silently become 172.18.0.0/16 and leave
                // the configuration reading as though one host were trusted.
                if (!IPNetwork.TryParse(range, out _))
                {
                    failures.Add(
                        $"Network:TrustedNetworks contains '{range}', which is not a CIDR " +
                        "range whose address is the start of the range, such as 172.18.0.0/16.");
                }
            }

            if (options.ForwardLimit < 1)
            {
                failures.Add(
                    $"Network:ForwardLimit must be at least 1. Found {options.ForwardLimit}.");
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }
    }
}
