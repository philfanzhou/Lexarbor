using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Authentication;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.RateLimiting;
using Npgsql;

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
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
            if (count == 0) break;
            if (buffer.Length + count > MaximumBodyBytes) return SettingsError.Write(413, "Settings request is too large.");
            buffer.Write(bytes, 0, count);
        }
        var changes = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
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
                if (invalid.Count > 0) throw new InvalidConfigurationCandidateException(invalid);
                // Nothing is published: the runtime keeps its startup values until the process restarts.
                return Task.FromResult<PreparedConfigurationChange?>(new(() => { }));
            }, context.RequestAborted, forNextStart: true);
        return result.Status switch
        {
            ConfigurationSaveStatus.Success => await reader.ReadAsync(context, CancellationToken.None),
            ConfigurationSaveStatus.MissingRevision => SettingsError.Write(428, "If-Match is required. Reload settings."),
            ConfigurationSaveStatus.InvalidRevision => SettingsError.Write(400, "Invalid If-Match. Reload settings."),
            ConfigurationSaveStatus.Conflict => SettingsError.Write(412, "Settings changed. Reload settings; offline edits require a restart."),
            ConfigurationSaveStatus.Overridden => Results.Json(new
            {
                message = "Settings are overridden by deployment configuration.",
                fields = result.OverriddenFields
            }, statusCode: 409),
            ConfigurationSaveStatus.InvalidCandidate => Results.Json(new
            {
                message = Invalid,
                fields = result.InvalidFields ?? changes.Keys.ToArray()
            }, statusCode: 400),
            _ => SettingsError.Write(503, ConfigurationSaveResult.StorageFailureMessage)
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

/// <summary>
/// Builds the catalog from one coordinator snapshot, so every entry describes the same
/// instant and shares the revision a later save must present.
/// </summary>
public sealed class ConfigurationCatalogReader(PersistentConfigurationCoordinator coordinator, PersistentConfigurationFileStore store,
    RateLimitRuntime rateLimits, AudioRuntimeConfiguration audio, EnrichmentRuntimeConfiguration enrichment,
    AdminOidcSettings login, IHostEnvironment environment)
{
    private static readonly string? StartedAt = ReadStart();

    public async Task<IResult> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var response = await coordinator.ReadAsync(Build, cancellationToken);
        context.Response.Headers.ETag = response.Revision;
        return Results.Json(response);
    }

    public ConfigurationCatalogEndpoints.CatalogResponse Build(ConfigurationReadResult read)
    {
        // The live modules' own answers, so the catalog never disagrees with their endpoints.
        var rate = SystemSettingsEndpoints.Response(read, rateLimits.Current);
        var storage = AudioStorageSettingsEndpoints.Response(read, audio.CopyCurrentOptions());
        var model = EnrichmentSettingsEndpoints.Body(read, enrichment.Current);
        return new(read.Revision, read.StorageAvailable, File.Exists(store.FilePath), read.ExternallyModified,
            ConfigurationCatalog.Fields.Select(field => Field(field, read, rate, storage, model)).ToArray(),
            ConfigurationCatalog.Groups.Select(group =>
                new ConfigurationCatalogEndpoints.GroupResponse(group.Id, group.Area, group.Name, group.Description)).ToArray(),
            ConfigurationCatalog.Sections.Select(section =>
                new ConfigurationCatalogEndpoints.SectionResponse(section.Group, section.Id, section.Name)).ToArray(),
            Facts(read), read.UnknownFileKeys);
    }

    private ConfigurationCatalogEndpoints.FieldResponse Field(ConfigurationCatalogField field, ConfigurationReadResult read,
        SystemSettingsEndpoints.RateLimitsResponse rate, AudioStorageSettingsEndpoints.AudioStorageResponse storage, JsonObject model)
    {
        // An environment variable is its own source; no settings request can carry it.
        var source = field.EnvironmentVariable
            ? new ConfigurationFieldSource(ConfigurationValueSource.Environment, false)
            : read.Sources[field.SourceKey];
        var editable = field.Apply != ConfigurationApply.ReadOnly && source.Editable;
        var reason = field.Apply == ConfigurationApply.ReadOnly ? field.Reason
            : source.Editable ? null : ConfigurationCatalog.DeploymentOverrideReason;
        JsonNode? value = null, saved = null;
        string? raw = null;
        bool? hasValue = null, savedHasValue = null;
        var catalog = read.Catalog.GetValueOrDefault(field.Key);
        if (field.Module is not null)
        {
            (value, hasValue) = Live(field, rate, storage, model);
        }
        else if (field.EnvironmentVariable)
        {
            hasValue = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(field.Key));
        }
        else if (catalog is not null)
        {
            if (field.Secret)
            {
                hasValue = !string.IsNullOrWhiteSpace(catalog.Text);
                // Whether the next start finds a secret, the way it finds whether this one did:
                // presence alone, so a saved replacement is never reported as nothing.
                if (field.Apply == ConfigurationApply.Restart) savedHasValue = !string.IsNullOrWhiteSpace(catalog.NextStartText);
            }
            else if (field.Key == ConfigurationCatalog.ConnectionString)
            {
                // The server and the database alone: a connection string carries a password meant for no browser.
                value = JsonValue.Create(DatabaseLocation(catalog.Text));
            }
            else if (field.Items)
            {
                // What this process runs with: the configured items, else the default.
                var running = new List<string?>();
                for (var index = 0; catalog.Children.TryGetValue(Index(index), out var item); index++) running.Add(item);
                value = Items(field, running.Count > 0 ? running.ToArray() : DefaultItems(field));
            }
            else
            {
                // The effective value: the configured text, or the default while it is absent.
                value = Typed(field, catalog.Text ?? field.Default);
                raw = catalog.Text;
            }
            // What the next start will read, as the coordinator decides it: the deployment's
            // own value, else the file's, else the default. A secret is never serialized.
            if (field.Apply == ConfigurationApply.Restart && !field.Secret)
            {
                saved = field.Items
                    ? Items(field, catalog.NextStartItems ?? DefaultItems(field))
                    : Typed(field, catalog.NextStartText ?? field.Default);
            }
        }
        // A model connection the deployment still reads from its section before the upgrade: said here,
        // because the file holds it under another name than the one shown.
        var location = field.ModuleKey is null ? null : coordinator.PhysicalKey(field.ModuleKey);
        if (location is null || location.Equals(field.Key, StringComparison.OrdinalIgnoreCase)) location = null;
        return new(field.Key, field.Group, field.Section, field.Name, field.Description,
            Kind(field.Kind), Apply(field.Apply), editable, SourceName(source.Source), reason,
            value, raw, hasValue, savedHasValue, saved, field.Secret ? null : field.Items ? Items(field, DefaultItems(field)) : Typed(field, field.Default),
            read.PendingRestart.Contains(field.Key),
            field.Minimum, field.Maximum, field.Nullable, field.Options, field.Module, field.Wire, location);
    }

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    // A live key's value is whatever its module has published, not what startup read.
    private static (JsonNode? Value, bool? HasValue) Live(ConfigurationCatalogField field,
        SystemSettingsEndpoints.RateLimitsResponse rate, AudioStorageSettingsEndpoints.AudioStorageResponse storage, JsonObject model)
    {
        switch (field.Module)
        {
            case "rateLimits":
                return (JsonValue.Create(rate.AdminLogin.Enabled), null);
            case "audioStorage":
                return field.Wire switch
                {
                    "accessKey" => (null, storage.HasAccessKey),
                    "secretKey" => (null, storage.HasSecretKey),
                    "enabled" => (JsonValue.Create(storage.Enabled), null),
                    "internalEndpoint" => (JsonValue.Create(storage.InternalEndpoint), null),
                    "publicEndpoint" => (JsonValue.Create(storage.PublicEndpoint), null),
                    "region" => (JsonValue.Create(storage.Region), null),
                    "bucket" => (JsonValue.Create(storage.Bucket), null),
                    _ => (JsonValue.Create(storage.KeyPrefix), null)
                };
            default:
                // Credential fields are reported as presence only, under the module's hasApiKey name.
                var node = Get(model, field.Secret ? field.Wire![..^6] + "hasApiKey" : field.Wire!);
                return field.Secret ? (null, node?.GetValue<bool>()) : (node?.DeepClone(), null);
        }
    }

    private static JsonNode? Get(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
        {
            node = node is JsonObject parent ? parent[part] : null;
        }
        return node;
    }

    // A list as its own kind: a list of numbers is reported as numbers, in a file or in a request.
    private static JsonArray Items(ConfigurationCatalogField field, IEnumerable<string?> texts)
    {
        var items = new JsonArray();
        foreach (var text in texts)
        {
            items.Add(field.Kind == ConfigurationValueKind.IntegerList
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? JsonValue.Create(number) : JsonValue.Create(text));
        }
        return items;
    }

    // A list's default, in configuration text form: comma-separated.
    private static string?[] DefaultItems(ConfigurationCatalogField field) =>
        string.IsNullOrEmpty(field.Default) ? [] : field.Default.Split(',', StringSplitOptions.TrimEntries);

    private ConfigurationCatalogEndpoints.FactResponse[] Facts(ConfigurationReadResult read) =>
    [
        new("version", "Application version", ApplicationVersion.Current,
            $"{ApplicationVersion.Channel} channel, revision {ApplicationVersion.Revision ?? "unknown"}"),
        new("environment", "Environment", environment.EnvironmentName, null),
        new("container", "Running in a container",
            PersistentConfigurationBootstrapper.IsRunningInContainer() ? "true" : "false", null),
        new("httpPort", "HTTP listen port", ApplicationFacts.HttpPort.ToString(CultureInfo.InvariantCulture), "HTTP/1 only"),
        new("serviceName", "Service name", ApplicationFacts.ServiceName, "Fixed"),
        new("database", "Database", "PostgreSQL", DatabaseLocation(read.Catalog.GetValueOrDefault(ConfigurationCatalog.ConnectionString)?.Text)),
        new("configurationFile", "Configuration file", ApplicationFacts.ConfigurationFile,
            File.Exists(store.FilePath) ? "Created" : "Not created yet"),
        new("administratorSignIn", "Administrator sign-in", login.Available ? "configured" : "not configured",
            AdminOidcSettings.CallbackPath),
        // Durable version metadata for the model configuration; not supplier availability.
        new("enrichmentConfigurationReady", "Enrichment configuration versions",
            enrichment.Current.ConfigurationReady ? "ready" : "pending", "Established by an authorized save or an enabled start"),
        new("startedAt", "Process started (UTC)", StartedAt, null)
    ];

    // The server and the database alone: a PostgreSQL connection string carries a password meant
    // for no browser, and a value that does not parse is reported as nothing at all.
    internal static string DatabaseLocation(string? text)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(text ?? "");
            return string.Join("; ", new[]
            {
                builder.Host is { Length: > 0 } host ? "Host=" + host : null,
                // PostgreSQL's own port is left unsaid, as a connection string usually does.
                builder.Port != 5432 ? "Port=" + builder.Port.ToString(CultureInfo.InvariantCulture) : null,
                builder.Database is { Length: > 0 } database ? "Database=" + database : null
            }.Where(part => part is not null));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            return string.Empty;
        }
    }

    // A configuration text in the value's own type; null when the text does not parse.
    private static JsonNode? Typed(ConfigurationCatalogField field, string? text) => text is null ? null : field.Kind switch
    {
        ConfigurationValueKind.Integer => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
            ? JsonValue.Create(whole) : null,
        ConfigurationValueKind.Number => decimal.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out var exact) ? JsonValue.Create(exact) : null,
        ConfigurationValueKind.Boolean or ConfigurationValueKind.TriState => bool.TryParse(text, out var flag) ? JsonValue.Create(flag) : null,
        _ => JsonValue.Create(text)
    };

    private static string Kind(ConfigurationValueKind kind) => kind switch
    {
        ConfigurationValueKind.TriState => "triState",
        ConfigurationValueKind.IntegerList => "integerList",
        _ => kind.ToString().ToLowerInvariant()
    };
    private static string Apply(ConfigurationApply apply) =>
        apply == ConfigurationApply.ReadOnly ? "readOnly" : apply.ToString().ToLowerInvariant();
    private static string SourceName(ConfigurationValueSource source) =>
        source == ConfigurationValueSource.CommandLine ? "commandLine" : source.ToString().ToLowerInvariant();

    private static string? ReadStart()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
