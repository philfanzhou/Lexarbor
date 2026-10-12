using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;

namespace Lexarbor.Host;

public static class AudioStorageSettingsEndpoints
{
    private const string InvalidMessage = "Invalid audio storage settings.";
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["enabled"] = "Enabled",
        ["internalEndpoint"] = "InternalEndpoint",
        ["publicEndpoint"] = "PublicEndpoint",
        ["region"] = "Region",
        ["bucket"] = "Bucket",
        ["keyPrefix"] = "KeyPrefix",
        ["accessKey"] = "AccessKey",
        ["secretKey"] = "SecretKey"
    };
    public sealed record FieldResponse(string Source, bool Editable, string? Reason);
    public sealed record AudioStorageResponse(string Revision, bool Enabled, string InternalEndpoint, string PublicEndpoint,
        string Region, string Bucket, string KeyPrefix, bool HasAccessKey, bool HasSecretKey, IReadOnlyDictionary<string, FieldResponse> Fields);

    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("", ReadAsync);
        group.MapPut("", SaveAsync);
    }

    private static AudioStorageResponse Response(ConfigurationReadResult read, AudioOptions options)
    {
        var fields = Names.ToDictionary(item => item.Key, item =>
        {
            var field = read.Sources["Audio:" + item.Value];
            var source = field.Source == ConfigurationValueSource.CommandLine ? "commandLine" : field.Source.ToString().ToLowerInvariant();
            return new FieldResponse(source, field.Editable,
                field.Editable ? null : "Overridden by deployment configuration.");
        });
        // Never serialize AudioOptions: it contains the current credentials.
        return new(read.Revision, options.Enabled, options.InternalEndpoint, options.PublicEndpoint, options.Region,
            options.Bucket, options.KeyPrefix, !string.IsNullOrWhiteSpace(options.AccessKey), !string.IsNullOrWhiteSpace(options.SecretKey), fields);
    }

    private static async Task<IResult> ReadAsync(HttpContext context, PersistentConfigurationCoordinator coordinator, AudioRuntimeConfiguration runtime)
        => await ReadResponseAsync(context, coordinator, runtime, context.RequestAborted);

    private static async Task<IResult> ReadResponseAsync(HttpContext context, PersistentConfigurationCoordinator coordinator,
        AudioRuntimeConfiguration runtime, CancellationToken cancellationToken)
    {
        var response = await coordinator.ReadAsync(read => Response(read, runtime.CopyCurrentOptions()), cancellationToken);
        context.Response.Headers.ETag = response.Revision;
        return Results.Json(response);
    }

    private static async Task<IResult> SaveAsync(HttpContext context, PersistentConfigurationCoordinator coordinator, AudioRuntimeConfiguration runtime)
    {
        // Manual bounded parsing keeps credential-bearing DTOs and binder diagnostics
        // out of model binding, response objects, and request-body logging.
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
            if (count == 0) break;
            if (buffer.Length + count > 16384) return Error(413, "Settings request is too large.");
            buffer.Write(bytes, 0, count);
        }
        var changes = new Dictionary<string, JsonNode?>();
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Error(400, InvalidMessage);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateObject())
            {
                if (!Names.TryGetValue(item.Name, out var name) || !seen.Add(item.Name)) return Error(400, InvalidMessage);
                var key = "Audio:" + name;
                if (item.Name is "accessKey" or "secretKey")
                {
                    if (!Credential(item.Value, out var action, out var value)) return Error(400, InvalidMessage);
                    if (action != "keep") changes.Add(key, JsonValue.Create(action == "clear" ? "" : value));
                }
                else if (item.Name == "enabled")
                {
                    if (item.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Error(400, InvalidMessage);
                    changes.Add(key, JsonValue.Create(item.Value.GetBoolean()));
                }
                else
                {
                    if (item.Value.ValueKind != JsonValueKind.String) return Error(400, InvalidMessage);
                    changes.Add(key, JsonValue.Create(item.Value.GetString()));
                }
            }
            if (seen.Count == 0) return Error(400, InvalidMessage);
        }
        catch (JsonException) { return Error(400, InvalidMessage); }

        // Capture effective retained values with the shared revision. Completing the
        // editable Audio fields also preserves keep on a first save without a template.
        // A concurrent module save makes If-Match stale before this patch can commit.
        var retained = await coordinator.ReadAsync(read => (Read: read, Options: runtime.CopyCurrentOptions()), context.RequestAborted);
        var fields = CurrentFields(retained.Options);
        foreach (var field in fields.Keys.ToArray())
            if (!retained.Read.Sources[field].Editable) fields.Remove(field);
        foreach (var change in changes) fields[change.Key] = change.Value;
        // An explicit keep is allowed on a locked credential; a requested replacement
        // or clear is rejected by the coordinator before constructing a client.
        var revision = context.Request.Headers.IfMatch;
        var result = await coordinator.SaveAsync(revision.Count == 0 ? null : revision.ToString(), fields, runtime.PrepareAsync, context.RequestAborted, allowEmptyFields: true);
        return result.Status switch
        {
            ConfigurationSaveStatus.Success => await ReadResponseAsync(context, coordinator, runtime, CancellationToken.None),
            ConfigurationSaveStatus.MissingRevision => Error(428, "If-Match is required. Reload settings."),
            ConfigurationSaveStatus.InvalidRevision => Error(400, "Invalid If-Match. Reload settings."),
            ConfigurationSaveStatus.Conflict => Error(412, "Settings changed. Reload settings; offline edits require a restart."),
            ConfigurationSaveStatus.Overridden => Results.Json(new
            {
                message = "Settings are overridden by deployment configuration.",
                fields = result.OverriddenFields?.Select(key => Names.Single(item => "Audio:" + item.Value == key).Key)
            }, statusCode: 409),
            ConfigurationSaveStatus.ResourceConflict => Error(409, "Existing audio or active operations require a separate storage migration."),
            ConfigurationSaveStatus.InvalidCandidate => Error(400, AudioOptions.ConfigurationFailure),
            _ => Error(503, ConfigurationSaveResult.StorageFailureMessage)
        };
    }

    private static Dictionary<string, JsonNode?> CurrentFields(AudioOptions options) => new()
    {
        ["Audio:Enabled"] = JsonValue.Create(options.Enabled),
        ["Audio:InternalEndpoint"] = JsonValue.Create(options.InternalEndpoint),
        ["Audio:PublicEndpoint"] = JsonValue.Create(options.PublicEndpoint),
        ["Audio:Region"] = JsonValue.Create(options.Region),
        ["Audio:Bucket"] = JsonValue.Create(options.Bucket),
        ["Audio:KeyPrefix"] = JsonValue.Create(options.KeyPrefix),
        ["Audio:AccessKey"] = JsonValue.Create(options.AccessKey),
        ["Audio:SecretKey"] = JsonValue.Create(options.SecretKey)
    };

    private static bool Credential(JsonElement element, out string? action, out string? value)
    {
        action = null; value = null;
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateObject())
        {
            if (!seen.Add(item.Name) || item.Value.ValueKind != JsonValueKind.String) return false;
            if (item.Name == "action") action = item.Value.GetString();
            else if (item.Name == "value") value = item.Value.GetString();
            else return false;
        }
        return action switch
        {
            "replace" => !string.IsNullOrWhiteSpace(value),
            "keep" or "clear" => !seen.Contains("value"),
            _ => false
        };
    }

    private static IResult Error(int status, string message) => Results.Json(new { message }, statusCode: status);
}
