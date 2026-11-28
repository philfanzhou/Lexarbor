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
        group.MapPost("/speech-preview", SpeechPreviewEndpoints.GenerateAsync);
    }
    private static async Task<IResult> ReadAsync(HttpContext context, PersistentConfigurationCoordinator coordinator, EnrichmentRuntimeConfiguration runtime)
        => await ReadResponse(context, coordinator, runtime, context.RequestAborted);
    private static async Task<IResult> ReadResponse(HttpContext context, PersistentConfigurationCoordinator coordinator,
        EnrichmentRuntimeConfiguration runtime, CancellationToken cancellationToken)
    {
        var response = await coordinator.ReadAsync(read => Body(read, runtime.Current), cancellationToken);
        context.Response.Headers.ETag = response["revision"]!.GetValue<string>();
        return Results.Json(response);
    }

    // Shared with the configuration catalog, so the two surfaces cannot disagree.
    internal static JsonObject Body(ConfigurationReadResult read, EnrichmentConfigurationSnapshot current)
    {
        var body = new JsonObject { ["revision"] = read.Revision, ["configurationReady"] = current.ConfigurationReady };
        var sources = new JsonObject();
        foreach (var field in EnrichmentConfigurationFields.ApiFields)
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
        // Older clients read these capability indicators. Neither is a configuration field.
        Set(body, ["enrichment", "fieldsEnabled"], current.Enrichment.FieldsEnabled);
        Set(body, ["enrichment", "audioEnabled"], current.Enrichment.AudioEnabled);
        Set(body, ["enrichment", "tts", "provider"], current.Enrichment.Provider);
        foreach (var field in new[] { "enrichment.fieldsEnabled", "enrichment.audioEnabled" })
            sources[field] = new JsonObject
            {
                ["source"] = "default",
                ["editable"] = false,
                ["reason"] = "Determined by model configuration."
            };
        sources["enrichment.tts.provider"] = new JsonObject
        {
            ["source"] = "default",
            ["editable"] = false,
            ["reason"] = "Determined by the speech endpoint."
        };
        body["fields"] = sources;
        return body;
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
        var body = await SettingsRequest.ReadBodyAsync(context, 16384, context.RequestAborted);
        if (body is null) return SettingsError.Write(413, "Settings request is too large.");
        var changes = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            if (!ReadChanges(document.RootElement, "", changes, supplied) || supplied.Count == 0) return SettingsError.Write(400, Invalid);
        }
        catch (JsonException) { return SettingsError.Write(400, Invalid); }
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
            ConfigurationSaveStatus.Overridden => SettingsRequest.Overridden(
                result.OverriddenFields?.Select(key => EnrichmentConfigurationFields.Find(key)!.Name)),
            ConfigurationSaveStatus.InvalidCandidate => SettingsError.Write(400, Invalid),
            _ => SettingsRequest.SaveFailure(result.Status)
        };
    }
    internal static bool ReadChanges(JsonElement element, string prefix, Dictionary<string, JsonNode?> changes, HashSet<string> supplied)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return false;
            var name = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            if (name == "enrichment.tts.provider")
            {
                if (!supplied.Add(name) || property.Value.ValueKind != JsonValueKind.String
                    || property.Value.GetString() is not ("MiniMaxNative" or "OpenAICompatible")) return false;
                continue;
            }
            if (name is "enrichment.fieldsEnabled" or "enrichment.audioEnabled")
            {
                if (!supplied.Add(name) || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                continue;
            }
            var field = EnrichmentConfigurationFields.ApiFields.FirstOrDefault(f => f.Name == name);
            if (field is null)
            {
                if (!EnrichmentConfigurationFields.ApiFields.Any(f => f.Name.StartsWith(name + ".", StringComparison.Ordinal)) || !ReadChanges(property.Value, name, changes, supplied)) return false;
                continue;
            }
            if (!supplied.Add(field.Name)) return false;
            JsonNode? candidate;
            if (field.Secret)
            {
                if (!SettingsRequest.TryReadCredential(property.Value, 4096, out var action, out var value)) return false;
                if (action == "keep") continue;
                candidate = JsonValue.Create(action == "clear" ? "" : value);
            }
            else
            {
                candidate = JsonNode.Parse(property.Value.GetRawText());
                if (!field.Valid(candidate)) return false;
            }
            if (!changes.TryAdd(field.Key, candidate) && !JsonNode.DeepEquals(changes[field.Key], candidate)) return false;
        }
        return seen.Count > 0;
    }
}
