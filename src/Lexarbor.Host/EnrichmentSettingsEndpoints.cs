using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Host.Configuration;

namespace Lexarbor.Host;

public static class EnrichmentSettingsEndpoints
{
    public const string Path = "/admin/enrichment/settings";
    private const string Invalid = "Invalid enrichment settings.";
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("", ReadAsync);
        group.MapPut("", SaveAsync);
    }
    private static async Task<IResult> ReadAsync(HttpContext context, PersistentConfigurationCoordinator coordinator, EnrichmentRuntimeConfiguration runtime)
        => await ReadResponse(context, coordinator, runtime, context.RequestAborted);
    private static async Task<IResult> ReadResponse(HttpContext context, PersistentConfigurationCoordinator coordinator,
        EnrichmentRuntimeConfiguration runtime, CancellationToken cancellationToken)
    {
        var response = await coordinator.ReadAsync(read =>
        {
            var current = runtime.Current;
            var body = new JsonObject { ["revision"] = read.Revision, ["configurationReady"] = current.ConfigurationReady };
            var sources = new JsonObject();
            foreach (var field in EnrichmentConfigurationFields.All)
            {
                var source = read.Sources[field.Key];
                sources[field.Name] = new JsonObject
                {
                    ["source"] = source.Source == ConfigurationValueSource.CommandLine ? "commandLine" : source.Source.ToString().ToLowerInvariant(),
                    ["editable"] = source.Editable,
                    ["reason"] = source.Editable ? null : "Overridden by deployment configuration."
                };
                var name = field.Secret ? field.Name[..^6] + "hasApiKey" : field.Name;
                Set(body, name.Split('.'), field.Secret
                    ? JsonValue.Create(!string.IsNullOrWhiteSpace(current.Fields[field.Key]!.GetValue<string>()))
                    : current.Fields[field.Key]?.DeepClone());
            }
            body["fields"] = sources;
            return body;
        }, cancellationToken);
        context.Response.Headers.ETag = response["revision"]!.GetValue<string>();
        return Results.Json(response);
    }
    private static void Set(JsonObject root, string[] names, JsonNode? value)
    {
        foreach (var name in names[..^1])
        {
            if (root[name] is not JsonObject child) root[name] = child = new JsonObject();
            root = child;
        }
        root[names[^1]] = value;
    }
    private static async Task<IResult> SaveAsync(HttpContext context, PersistentConfigurationCoordinator coordinator, EnrichmentRuntimeConfiguration runtime)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
            if (count == 0) break;
            if (buffer.Length + count > 16384) return Error(413, "Settings request is too large.");
            buffer.Write(bytes, 0, count);
        }
        var changes = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var supplied = 0;
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            bool Visit(JsonElement element, string prefix)
            {
                if (element.ValueKind != JsonValueKind.Object) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) return false;
                    var name = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                    var field = EnrichmentConfigurationFields.All.FirstOrDefault(f => f.Name == name);
                    if (field is null)
                    {
                        if (!EnrichmentConfigurationFields.All.Any(f => f.Name.StartsWith(name + ".", StringComparison.Ordinal)) || !Visit(property.Value, name)) return false;
                        continue;
                    }
                    supplied++;
                    if (field.Secret)
                    {
                        if (!Credential(property.Value, out var action, out var value)) return false;
                        if (action != "keep") changes.Add(field.Key, JsonValue.Create(action == "clear" ? "" : value));
                    }
                    else
                    {
                        var value = JsonNode.Parse(property.Value.GetRawText());
                        if (!field.Valid(value)) return false;
                        changes.Add(field.Key, value);
                    }
                }
                return seen.Count > 0;
            }
            if (!Visit(document.RootElement, "") || supplied == 0) return Error(400, Invalid);
        }
        catch (JsonException) { return Error(400, Invalid); }
        var retained = await coordinator.ReadAsync(read => (Read: read, Snapshot: runtime.Current), context.RequestAborted);
        var fields = retained.Snapshot.Fields.Where(f => retained.Read.Sources[f.Key].Editable)
            .ToDictionary(f => f.Key, f => f.Value?.DeepClone(), StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes) fields[change.Key] = change.Value;
        var revision = context.Request.Headers.IfMatch;
        var result = await coordinator.SaveAsync(revision.Count == 0 ? null : revision.ToString(), fields, runtime.PrepareAsync,
            context.RequestAborted, allowEmptyFields: true);
        return result.Status switch
        {
            ConfigurationSaveStatus.Success => await ReadResponse(context, coordinator, runtime, CancellationToken.None),
            ConfigurationSaveStatus.MissingRevision => Error(428, "If-Match is required. Reload settings."),
            ConfigurationSaveStatus.InvalidRevision => Error(400, "Invalid If-Match. Reload settings."),
            ConfigurationSaveStatus.Conflict => Error(412, "Settings changed. Reload settings; offline edits require a restart."),
            ConfigurationSaveStatus.Overridden => Results.Json(new
            {
                message = "Settings are overridden by deployment configuration.",
                fields = result.OverriddenFields?.Select(key => EnrichmentConfigurationFields.Find(key)!.Name)
            }, statusCode: 409),
            ConfigurationSaveStatus.InvalidCandidate => Error(400, Invalid),
            _ => Error(503, ConfigurationSaveResult.StorageFailureMessage)
        };
    }
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
            "replace" => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096,
            "keep" or "clear" => !seen.Contains("value"),
            _ => false
        };
    }
    private static IResult Error(int status, string message) => Results.Json(new { message }, statusCode: status);
}
