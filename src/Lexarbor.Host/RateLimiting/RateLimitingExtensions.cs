using System.Globalization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ServiceMantle;
using ServiceMantle.Web;
using SharedPolicy = ServiceMantle.Web.RateLimiting.RateLimitPolicyOptions;

namespace Lexarbor.Host.RateLimiting;

public static class RateLimitingExtensions
{
    public const string AdminLoginPolicy = "admin-login";
    public const string PublicApiPolicy = "public-api";

    public static void AddLexarborRateLimiting(
        this ServiceMantleBuilder serviceMantle,
        IConfiguration configuration)
    {
        // Valid placeholders survive disabled policies. Options bind after the
        // test host's late configuration, before the shared startup snapshot.
        var adminLogin = new SharedPolicy { PermitLimit = 10, Window = TimeSpan.FromSeconds(300) };
        var publicApi = new SharedPolicy { PermitLimit = 300, Window = TimeSpan.FromSeconds(60) };
        serviceMantle.AddRateLimiting(options =>
        {
            options.ConsumerPolicies.Add(AdminLoginPolicy, adminLogin);
            options.ConsumerPolicies.Add(PublicApiPolicy, publicApi);
        });
        serviceMantle.Services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .PostConfigure(options =>
            {
                Apply(options.AdminLogin, adminLogin);
                Apply(options.PublicApi, publicApi);
            });
    }

    private static void Apply(RateLimitPolicyOptions configured, SharedPolicy shared)
    {
        if (configured.Enabled)
        {
            shared.PermitLimit = configured.PermitLimit;
            shared.Window = TimeSpan.FromSeconds(configured.WindowSeconds);
        }
    }

    public static IApplicationBuilder UseLexarborRateLimitRetryAfter(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var metadata = context.GetEndpoint()?.Metadata;
            var policy = metadata?.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            if (metadata?.GetMetadata<DisableRateLimitingAttribute>() is null
                && policy is AdminLoginPolicy or PublicApiPolicy)
            {
                var options = context.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value;
                var seconds = policy == AdminLoginPolicy
                    ? options.AdminLogin.WindowSeconds
                    : options.PublicApi.WindowSeconds;
                context.Response.OnStarting(() =>
                {
                    // Sliding-window leases currently omit RetryAfter metadata.
                    // Keep any shared header; a whole window is conservative advice.
                    if (context.Response.StatusCode == StatusCodes.Status429TooManyRequests
                        && !context.Response.Headers.ContainsKey("Retry-After"))
                    {
                        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                    }
                    return Task.CompletedTask;
                });
            }
            await next(context);
        });
    }
}
