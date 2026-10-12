using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.RateLimiting;
using ServiceMantle.Web.Management;

namespace Lexarbor.Host;

public static class SystemSettingsEndpoints
{
    private const string Path = "/admin/system/settings/rate-limits";
    private sealed class SettingsMetadata;
    public sealed record PolicyResponse(bool Enabled, int PermitLimit, int WindowSeconds, string Source, bool Editable);
    public sealed record RateLimitsResponse(string Revision, PolicyResponse AdminLogin, PolicyResponse PublicApi);

    public static IApplicationBuilder UseSystemSettingsNoStore(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<SettingsMetadata>() is not null)
            context.Response.Headers.CacheControl = "no-store";
        await next(context);
    });

    public static void MapSystemSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Path).RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName)
            .WithMetadata(new SettingsMetadata());
        group.MapGet("", ReadAsync);
        group.MapPut("", SaveAsync);
    }

    private static async Task<IResult> ReadAsync(HttpContext context, PersistentConfigurationCoordinator coordinator, RateLimitRuntime runtime)
    {
        var response = await coordinator.ReadAsync(result => Response(result, runtime.Current), context.RequestAborted);
        context.Response.Headers.ETag = response.Revision;
        return Results.Json(response);
    }

    private static RateLimitsResponse Response(ConfigurationReadResult read, RateLimitSnapshot snapshot)
    {
        PolicyResponse Policy(string name, RateLimitPolicySnapshot policy)
        {
            var source = read.Sources[$"RateLimits:{name}:Enabled"];
            var sourceName = source.Source == ConfigurationValueSource.CommandLine ? "commandLine" : source.Source.ToString().ToLowerInvariant();
            return new(policy.Enabled, policy.PermitLimit, policy.WindowSeconds, sourceName, source.Editable);
        }
        return new(read.Revision, Policy("AdminLogin", snapshot.AdminLogin), Policy("PublicApi", snapshot.PublicApi));
    }

    private static async Task<IResult> SaveAsync(HttpContext context, PersistentConfigurationCoordinator coordinator, RateLimitRuntime runtime)
    {
        // Bound the tiny switch payload before parsing, including chunked requests.
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
            if (count == 0) break;
            if (buffer.Length + count > 4096) return Error(413, "Settings request is too large.");
            buffer.Write(bytes, 0, count);
        }
        var fields = new Dictionary<string, JsonNode?>();
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Error(400, "Invalid rate limit settings.");
            foreach (var item in document.RootElement.EnumerateObject())
            {
                var key = item.Name switch
                {
                    "adminLoginEnabled" => "RateLimits:AdminLogin:Enabled",
                    "publicApiEnabled" => "RateLimits:PublicApi:Enabled",
                    _ => null
                };
                if (key is null || item.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !fields.TryAdd(key, JsonValue.Create(item.Value.GetBoolean())))
                    return Error(400, "Invalid rate limit settings.");
            }
            if (fields.Count == 0) return Error(400, "Invalid rate limit settings.");
        }
        catch (JsonException) { return Error(400, "Invalid rate limit settings."); }
        var revision = context.Request.Headers.IfMatch;
        var result = await coordinator.SaveAsync(revision.Count == 0 ? null : revision.ToString(), fields, (configuration, _) =>
        {
            var options = new RateLimitOptions();
            configuration.GetSection(RateLimitOptions.SectionName).Bind(options);
            if ((options.AdminLogin.Enabled && !RateLimitingExtensions.ValidNumbers(options.AdminLogin))
                || (options.PublicApi.Enabled && !RateLimitingExtensions.ValidNumbers(options.PublicApi)))
                return Task.FromResult<PreparedConfigurationChange?>(null);
            var candidate = RateLimitSnapshot.From(options);
            return Task.FromResult<PreparedConfigurationChange?>(new(() => runtime.Publish(candidate)));
        }, context.RequestAborted);
        return result.Status switch
        {
            ConfigurationSaveStatus.Success => await ReadCommittedAsync(context, coordinator, runtime),
            ConfigurationSaveStatus.MissingRevision => Error(428, "If-Match is required. Reload settings."),
            ConfigurationSaveStatus.InvalidRevision => Error(400, "Invalid If-Match. Reload settings."),
            ConfigurationSaveStatus.Conflict => Error(412, "Settings changed. Reload settings; offline edits require a restart."),
            ConfigurationSaveStatus.Overridden => Results.Json(new { message = "Settings are overridden by deployment configuration.", fields = result.OverriddenFields }, statusCode: 409),
            ConfigurationSaveStatus.InvalidCandidate => Error(400, "Invalid rate limit settings. Check the configured permit limit and window."),
            _ => Error(503, ConfigurationSaveResult.StorageFailureMessage)
        };
    }

    private static async Task<IResult> ReadCommittedAsync(HttpContext context, PersistentConfigurationCoordinator coordinator, RateLimitRuntime runtime)
    {
        // The file was committed: client cancellation must not interrupt publication or its confirmation snapshot.
        var response = await coordinator.ReadAsync(result => Response(result, runtime.Current), CancellationToken.None);
        context.Response.Headers.ETag = response.Revision;
        return Results.Json(response);
    }

    private static IResult Error(int status, string message) => Results.Json(new { message }, statusCode: status);
}
