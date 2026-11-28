using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.Configuration;

namespace Lexarbor.Host;

/// <summary>
/// The complete configuration surface. The catalog reports every key Lexarbor reads with its
/// effective value, its source, and whether a change applies at once or at the next start; the
/// startup endpoint persists the latter. Live keys keep their module endpoints.
/// </summary>
public static class ConfigurationCatalogEndpoints
{
    private const int MaximumBodyBytes = 16384;
    private const string Invalid = "Invalid startup settings.";

    public sealed record FieldResponse(string Key, string Group, string Section, string Name, string? Description,
        string Kind, string Apply, bool Editable, string Source, string? Reason,
        JsonNode? Value, string? Raw, bool? HasValue, bool? SavedHasValue, JsonNode? SavedValue, JsonNode? DefaultValue,
        bool PendingRestart, decimal? Minimum, decimal? Maximum, bool Nullable, IReadOnlyList<string>? Options,
        string? Module, string? Wire, string? Location);
    public sealed record GroupResponse(string Id, string Area, string Name, string? Description);
    public sealed record SectionResponse(string Group, string Id, string Name);
    public sealed record FactResponse(string Id, string Name, string? Value, string? Detail);
    public sealed record CatalogResponse(string Revision, bool StorageAvailable, bool FileExists, bool ExternallyModified,
        IReadOnlyList<FieldResponse> Fields, IReadOnlyList<GroupResponse> Groups, IReadOnlyList<SectionResponse> Sections,
        IReadOnlyList<FactResponse> Facts, IReadOnlyList<string> UnknownFileKeys);

    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/catalog", ReadAsync);
        group.MapPut("/startup", SaveStartupAsync);
    }

    /// <summary>
    /// The reader behind the catalog and the startup endpoint. Every host that maps the system
    /// settings endpoints needs it. It reads the sign-in settings (<see cref="AdminOidcSettings"/>),
    /// which the complete host binds in its admin registration.
    /// </summary>
    public static IServiceCollection AddConfigurationCatalog(this IServiceCollection services)
        => services.AddSingleton<ConfigurationCatalogReader>();

    private static Task<IResult> ReadAsync(HttpContext context, ConfigurationCatalogReader reader)
        => reader.ReadAsync(context, context.RequestAborted);

    private static async Task<IResult> SaveStartupAsync(HttpContext context, PersistentConfigurationCoordinator coordinator,
        ConfigurationCatalogReader reader, IHostEnvironment environment, AdminOidcSettings startupLogin)
    {
        // Manual bounded parsing keeps credential-bearing values out of model binding and
        // request logging, exactly like the live modules.
        var body = await SettingsRequest.ReadBodyAsync(context, MaximumBodyBytes, context.RequestAborted);
        if (body is null) return SettingsError.Write(413, "Settings request is too large.");
        var changes = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return SettingsError.Write(400, Invalid);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var field = ConfigurationCatalog.Find(property.Name);
                // Only restart-only keys belong here, and no key may appear twice.
                if (field is null || field.Apply != ConfigurationApply.Restart || !seen.Add(field.Key))
                    return SettingsError.Write(400, Invalid);
                var (keep, value) = Read(field, property.Value);
                if (!keep) changes.Add(field.Key, value);
            }
            if (changes.Count == 0) return SettingsError.Write(400, Invalid);
        }
        catch (JsonException) { return SettingsError.Write(400, Invalid); }
        var validator = new StartupConfigurationValidator(environment, startupLogin);
        var revision = context.Request.Headers.IfMatch;
        var result = await coordinator.SaveAsync(revision.Count == 0 ? null : revision.ToString(), changes,
            (candidate, token) =>
            {
                token.ThrowIfCancellationRequested();
                var invalid = validator.InvalidFields(candidate);
                if (invalid.Count > 0) throw new InvalidCandidateException(invalid);
                // Nothing is published: the runtime keeps its startup values until the process restarts.
                return Task.FromResult<PreparedConfigurationChange?>(new(() => { }));
            }, context.RequestAborted, forNextStart: true);
        return result.Status switch
        {
            ConfigurationSaveStatus.Success => await reader.ReadAsync(context, CancellationToken.None),
            ConfigurationSaveStatus.Overridden => SettingsRequest.Overridden(result.OverriddenFields),
            ConfigurationSaveStatus.InvalidCandidate => Results.Json(new
            {
                message = Invalid,
                fields = result.InvalidFields ?? changes.Keys.ToArray()
            }, statusCode: 400),
            _ => SettingsRequest.SaveFailure(result.Status)
        };
    }

    // One property of the request body. A null removes a persisted key, so the next start
    // reads the default; a credential's explicit keep changes nothing at all.
    private static (bool Keep, JsonNode? Value) Read(ConfigurationCatalogField field, JsonElement value) => field.Secret
        ? Credential(value) switch
        {
            ("keep", _) => (true, null),
            ("clear", _) => (false, JsonValue.Create("")),
            ("replace", var text) => (false, JsonValue.Create(text)),
            _ => throw new JsonException()
        }
        : (false, value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(value.GetRawText()));

    private static (string Action, string? Value) Credential(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        string? action = null, value = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.String || !names.Add(item.Name)) throw new JsonException();
            if (item.Name == "action") action = item.Value.GetString();
            else if (item.Name == "value") value = item.Value.GetString();
            else throw new JsonException();
        }
        return action switch
        {
            "replace" when !string.IsNullOrWhiteSpace(value) => ("replace", value),
            "keep" or "clear" when !names.Contains("value") => (action!, null),
            _ => throw new JsonException()
        };
    }
}
