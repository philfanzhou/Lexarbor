using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Lexarbor.Host.Configuration;

public enum ConfigurationValueSource { Default, File, Environment, CommandLine }
public enum ConfigurationSaveStatus { Success, MissingRevision, InvalidRevision, Conflict, Overridden, InvalidCandidate, StorageFailure, ResourceConflict }
public sealed record ConfigurationFieldSource(ConfigurationValueSource Source, bool Editable);
public sealed record ConfigurationReadResult(string Revision, IReadOnlyDictionary<string, ConfigurationFieldSource> Sources, bool StorageAvailable)
{
    /// <summary>Restart-only keys whose value at the next start differs from the one this process runs with.</summary>
    public IReadOnlySet<string> PendingRestart { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Startup-bound catalog keys: what this process runs with, and what the file holds for its next start.</summary>
    public IReadOnlyDictionary<string, ConfigurationCatalogValue> Catalog { get; init; } = new Dictionary<string, ConfigurationCatalogValue>();
    /// <summary>Leaf paths in the persisted file that the catalog does not describe; names only, never values.</summary>
    public IReadOnlyList<string> UnknownFileKeys { get; init; } = [];
    /// <summary>The file changed on disk since this process last read or wrote it; online saves wait for a restart.</summary>
    public bool ExternallyModified { get; init; }
}

/// <summary>One startup-bound key as this process started with it, and as the next start will read it.</summary>
public sealed record ConfigurationCatalogValue(string? Text, IReadOnlyDictionary<string, string?> Children, JsonNode? FileNode)
{
    /// <summary>What the next start reads for the key: the deployment's own value, else the file's, else the default.</summary>
    public string? NextStartText { get; init; }
    public IReadOnlyList<string?>? NextStartItems { get; init; }
}
public sealed record ConfigurationSaveResult(ConfigurationSaveStatus Status, string? Revision = null, IReadOnlyList<string>? OverriddenFields = null,
    IReadOnlyList<string>? InvalidFields = null)
{
    public const string StorageFailureMessage = "Persistent configuration is unavailable.";
}

public sealed class ConfigurationChangeConflictException() : Exception("Existing audio requires a separate storage migration.");

/// <summary>A module refused a candidate configuration; names the keys only, never their values.</summary>
public sealed class InvalidConfigurationCandidateException(IReadOnlyList<string> fields)
    : Exception("The candidate configuration is invalid.")
{
    public IReadOnlyList<string> Fields { get; } = fields;
}

/// <summary>The module owns validation and supplies a non-throwing, synchronous publication.</summary>
public sealed class PreparedConfigurationChange(Action publish, Action? discard = null,
    JsonObject? enrichmentVersions = null) : IDisposable
{
    private bool _published;
    internal JsonObject? EnrichmentVersions { get; } = enrichmentVersions;

    public void Publish()
    {
        publish();
        _published = true;
    }

    public void Dispose()
    {
        if (!_published)
        {
            discard?.Invoke();
        }
    }
}

/// <summary>One singleton serializes all settings modules and owns the shared file revision.</summary>
public sealed class PersistentConfigurationCoordinator
{
    // Only these fields may be edited online. Modules submit their complete target fields,
    // including retained credentials, never IConfiguration.AsEnumerable() as a file patch.
    // Restart-only fields join them: they are persisted for the next start, never published.
    private static readonly HashSet<string> AllowedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "RateLimits:AdminLogin:Enabled",
        "Audio:Enabled", "Audio:InternalEndpoint", "Audio:PublicEndpoint", "Audio:Region",
        "Audio:Bucket", "Audio:KeyPrefix", "Audio:AccessKey", "Audio:SecretKey"
    };
    static PersistentConfigurationCoordinator()
    {
        AllowedFields.UnionWith(EnrichmentConfigurationFields.All.Select(field => field.Key));
        AllowedFields.UnionWith(ConfigurationCatalog.RestartFields.Select(field => field.Key));
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IConfigurationRoot _configuration;
    private readonly ModelConfigurationPaths _modelPaths;
    private readonly PersistentConfigurationFileStore _store;
    private readonly Dictionary<string, string?> _startup;
    private readonly ILookup<string, KeyValuePair<string, string?>> _startupBySection;
    private readonly Dictionary<string, string?> _overrides;
    private Dictionary<string, ConfigurationFieldSource> _sources;
    private JsonObject? _lastValidDocument;
    // The file as this process started with it, before any online save.
    private JsonObject? _startupDocument;
    private byte[]? _diskBytes;
    private string _revision = NewRevision();
    // A disk edit is not a runtime edit. Reject writes until the host restarts,
    // rather than merging a new file underneath startup-only host settings.
    private bool _externalChange;

    public PersistentConfigurationCoordinator(IConfigurationRoot configuration, PersistentConfigurationFileStore store)
    {
        _configuration = configuration;
        var snapshot = store.Read();
        // A model connection is read from whichever section the deployment resolved: the one of this
        // release, or the section a deployment override keeps it in from before the upgrade.
        _modelPaths = ModelConfigurationPaths.Resolve(configuration, snapshot.Document ?? new JsonObject());
        _store = store;
        _startup = configuration.AsEnumerable().Where(item => !PrivateMetadata(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        _startupBySection = _startup.ToLookup(item => item.Key.Split(':')[0], StringComparer.OrdinalIgnoreCase);
        _overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _sources = new Dictionary<string, ConfigurationFieldSource>(StringComparer.OrdinalIgnoreCase);
        // Every catalog key carries its source, so the surface can also say why a
        // read-only one is read-only. A persisted list has only its index entries.
        foreach (var field in AllowedFields.Union(ConfigurationCatalog.Fields
                     .Where(field => field.Module is null && !field.EnvironmentVariable).Select(field => field.Key)))
        {
            var physicalKey = _modelPaths.PhysicalKey(field);
            var source = FindSource(ConfigurationCatalog.Find(field) is { Kind: ConfigurationValueKind.List } ? physicalKey + ":0" : physicalKey);
            _sources[field] = source;
        }
        foreach (var provider in configuration.Providers)
        {
            if (provider is not EnvironmentVariablesConfigurationProvider and not CommandLineConfigurationProvider)
            {
                continue;
            }
            foreach (var key in _startup.Keys)
            {
                if (provider.TryGet(key, out var value))
                {
                    _overrides[key] = value;
                }
            }
        }
        _lastValidDocument = snapshot.Document;
        _startupDocument = (JsonObject?)snapshot.Document?.DeepClone();
        _diskBytes = snapshot.Bytes;
    }

    public async Task<ConfigurationReadResult> ReadAsync(CancellationToken cancellationToken = default)
        => await ReadAsync(result => result, cancellationToken);

    // Read the module snapshot and its shared revision under the publication lock.
    public async Task<T> ReadAsync<T>(Func<ConfigurationReadResult, T> read, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var available = RefreshDisk();
            // One snapshot for everything a reader sees: the pending keys are decided from
            // the same next-start values the catalog reports.
            var catalog = CatalogValues();
            return read(new ConfigurationReadResult(_revision,
                new Dictionary<string, ConfigurationFieldSource>(_sources, StringComparer.OrdinalIgnoreCase), available)
            {
                PendingRestart = PendingRestart(catalog),
                Catalog = catalog,
                UnknownFileKeys = UnknownFileKeys(),
                ExternallyModified = _externalChange
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    // allowEmptyFields is reserved for a validated explicit keep when every module
    // field is deployment-locked; it must not turn an empty HTTP body into a save.
    // forNextStart marks a save of restart-only fields: its candidate is the next
    // start's configuration, while a live module's candidate keeps this process's
    // own values for those fields.
    public async Task<ConfigurationSaveResult> SaveAsync(
        string? revision,
        IReadOnlyDictionary<string, JsonNode?> fields,
        Func<IConfiguration, CancellationToken, Task<PreparedConfigurationChange?>> prepare,
        CancellationToken cancellationToken = default,
        bool allowEmptyFields = false,
        bool forNextStart = false)
    {
        if (revision is null)
        {
            return new(ConfigurationSaveStatus.MissingRevision);
        }
        if (!ValidRevision(revision))
        {
            return new(ConfigurationSaveStatus.InvalidRevision);
        }
        if ((!allowEmptyFields && fields.Count == 0) || fields.Keys.Any(key => !AllowedFields.Contains(key))
            || fields.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Count
            || fields.Any(field => !ValidField(field.Key, field.Value)))
        {
            return new(ConfigurationSaveStatus.InvalidCandidate);
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!RefreshDisk())
            {
                return new(ConfigurationSaveStatus.StorageFailure);
            }
            if (_externalChange || revision != _revision)
            {
                return new(ConfigurationSaveStatus.Conflict);
            }
            var overridden = fields.Keys.Where(key => !_sources[key].Editable).ToArray();
            if (overridden.Length > 0)
            {
                return new(ConfigurationSaveStatus.Overridden, OverriddenFields: overridden);
            }
            JsonObject document;
            try
            {
                document = (JsonObject)(_lastValidDocument?.DeepClone() ?? _store.ReadTemplate());
                foreach (var field in fields)
                {
                    SetField(document, _modelPaths.PhysicalKey(field.Key), field.Value?.DeepClone());
                }
            }
            catch (Exception exception) when (IsStorageError(exception))
            {
                return new(ConfigurationSaveStatus.StorageFailure);
            }
            PreparedConfigurationChange? candidate;
            try
            {
                var values = new ConfigurationBuilder().AddInMemoryCollection(_startup);
                // Overlay every file field, including other modules' successful saves. A live
                // module is shown the configuration it is actually running with: restart-only
                // values already saved for the next start stay at this process's own.
                values.AddJsonStream(new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    forNextStart ? document : Running(document))));
                values.AddInMemoryCollection(_overrides);
                var candidateConfiguration = values.Build();
                using var configurationLifetime = candidateConfiguration as IDisposable;
                candidate = await prepare(candidateConfiguration, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ConfigurationChangeConflictException)
            {
                return new(ConfigurationSaveStatus.ResourceConflict);
            }
            catch (InvalidConfigurationCandidateException exception)
            {
                // The module's own verdict, with the keys it refused.
                return new(ConfigurationSaveStatus.InvalidCandidate, InvalidFields: exception.Fields);
            }
            catch (Exception)
            {
                // Module validation/client construction failures must not leak raw values.
                return new(ConfigurationSaveStatus.InvalidCandidate);
            }
            if (candidate is null)
            {
                return new(ConfigurationSaveStatus.InvalidCandidate);
            }
            using (candidate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Private comparison metadata is produced by the trusted module, never
                // accepted as an HTTP field, and shares its single atomic commit point.
                if (candidate.EnrichmentVersions is not null)
                    SetField(document, "EnrichmentConfigurationVersions", candidate.EnrichmentVersions.DeepClone());
                byte[] bytes;
                try
                {
                    bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(CleanConfigurationForDisk(document),
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                }
                catch (Exception exception) when (IsStorageError(exception))
                {
                    return new(ConfigurationSaveStatus.StorageFailure);
                }
                var nextRevision = NewRevision();
                var nextSources = new Dictionary<string, ConfigurationFieldSource>(_sources, StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields)
                {
                    // A removed key is no longer in the file; the next start reads the default.
                    nextSources[field.Key] = field.Value is null
                        ? new(ConfigurationValueSource.Default, true)
                        : new(ConfigurationValueSource.File, true);
                }
                var success = new ConfigurationSaveResult(ConfigurationSaveStatus.Success, nextRevision);
                try
                {
                    // Detect disk changes during candidate preparation as well.
                    if (!RefreshDisk())
                    {
                        return new(ConfigurationSaveStatus.StorageFailure);
                    }
                    if (_externalChange)
                    {
                        return new(ConfigurationSaveStatus.Conflict);
                    }
                    await _store.CommitBytesAsync(bytes, cancellationToken);
                }
                catch (Exception exception) when (IsStorageError(exception))
                {
                    return new(ConfigurationSaveStatus.StorageFailure);
                }
                _lastValidDocument = document;
                _diskBytes = bytes;
                _revision = nextRevision;
                _sources = nextSources;
                // There is deliberately no token check after the atomic file replacement.
                candidate.Publish();
                return success;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool RefreshDisk()
    {
        try
        {
            var snapshot = _store.Read();
            if (snapshot.Bytes is null)
            {
                // Loss of the file/directory is recoverable using the last valid snapshot.
                _diskBytes = null;
                return true;
            }
            if (_diskBytes is null || !snapshot.Bytes.AsSpan().SequenceEqual(_diskBytes))
            {
                _diskBytes = snapshot.Bytes;
                _lastValidDocument = snapshot.Document;
                _externalChange = true;
                _revision = NewRevision();
            }
            return true;
        }
        catch (Exception exception) when (IsStorageError(exception))
        {
            return false;
        }
    }

    private ConfigurationFieldSource FindSource(string key)
    {
        foreach (var provider in _configuration.Providers.Reverse())
        {
            if (!provider.TryGet(key, out _))
            {
                continue;
            }
            if (provider is EnvironmentVariablesConfigurationProvider)
            {
                return new(ConfigurationValueSource.Environment, false);
            }
            if (provider is CommandLineConfigurationProvider)
            {
                return new(ConfigurationValueSource.CommandLine, false);
            }
            if (provider is JsonConfigurationProvider json)
            {
                return new(IsFile(json) ? ConfigurationValueSource.File : ConfigurationValueSource.Default, true);
            }
            return new(ConfigurationValueSource.Default, true);
        }
        return new(ConfigurationValueSource.Default, true);
    }

    // Whether a provider is the persisted data file rather than the image defaults,
    // the environment or the command line.
    private bool IsFile(IConfigurationProvider provider) => provider is JsonConfigurationProvider json
        && json.Source.FileProvider?.GetFileInfo(json.Source.Path!).PhysicalPath == _store.FilePath;

    // The next start composes defaults, then the file, then the environment and command
    // line. A key the deployment supplies keeps its value whatever the file holds, so it is
    // never waiting for a restart.
    private string? NextStart(string key) => Supplied(key) ? _startup.GetValueOrDefault(key)
        : Node(_lastValidDocument, key) is { } node ? TextOf(node) : WithoutFile(key);

    private bool Supplied(string key) => _overrides.Keys.Any(item =>
        item.Equals(key, StringComparison.OrdinalIgnoreCase) || item.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));

    // A file value in configuration text form: strings as themselves, booleans and numbers
    // spelled as the providers spell them.
    private static string? TextOf(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
        _ => node.ToJsonString()
    };

    private string? WithoutFile(string key)
    {
        foreach (var provider in _configuration.Providers.Reverse())
        {
            if (!IsFile(provider) && provider.TryGet(key, out var value))
            {
                return value;
            }
        }
        return null;
    }

    private string?[]? NextStartItems(string key) => Supplied(key) ? StartupItems(key)
        : Node(_lastValidDocument, key) is JsonArray array ? array.Select(TextOf).ToArray() : WithoutFileItems(key);

    private string?[]? WithoutFileItems(string key)
    {
        foreach (var provider in _configuration.Providers.Reverse())
        {
            if (IsFile(provider) || !provider.TryGet(key + ":0", out _))
            {
                continue;
            }
            var items = new List<string?>();
            for (var index = 0; provider.TryGet(key + ":" + index, out var value); index++)
            {
                items.Add(value);
            }
            return items.ToArray();
        }
        return null;
    }

    private string?[] StartupItems(string key)
    {
        var items = new List<string?>();
        for (var index = 0; _startup.TryGetValue(key + ":" + index, out var value); index++)
        {
            items.Add(value);
        }
        return items.ToArray();
    }

    // The value a key takes for the process: its text, or the default while it is absent or
    // blank. The providers and the file also spell booleans in different casings.
    private static string? Effective(ConfigurationCatalogField field, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) text = field.Default;
        return text is "true" or "false" ? text.ToLowerInvariant() : text;
    }

    // Two texts that spell the same number count as the same value: 0.90 is 0.9.
    private static bool Same(ConfigurationCatalogField field, string? next, string? running)
    {
        next = Effective(field, next);
        running = Effective(field, running);
        return decimal.TryParse(next, NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(running, NumberStyles.Number, CultureInfo.InvariantCulture, out var b) ? a == b
            : string.Equals(next, running, StringComparison.Ordinal);
    }

    // A restart-only key is pending while the next start would read another value than
    // the one this process runs with.
    private HashSet<string> PendingRestart(IReadOnlyDictionary<string, ConfigurationCatalogValue> catalog)
    {
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in ConfigurationCatalog.RestartFields)
        {
            var value = catalog[field.Key];
            var changed = field.Items
                ? !(value.NextStartItems ?? []).SequenceEqual(StartupItems(field.Key))
                : !Same(field, value.NextStartText, value.Text);
            if (changed)
            {
                pending.Add(field.Key);
            }
        }
        return pending;
    }

    // Startup-bound catalog keys only: a live key's value is whatever its module has
    // published, and an environment variable's value stays in the environment.
    private Dictionary<string, ConfigurationCatalogValue> CatalogValues()
    {
        var values = new Dictionary<string, ConfigurationCatalogValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in ConfigurationCatalog.Fields.Where(field => field.Module is null && !field.EnvironmentVariable))
        {
            var prefix = field.Key + ":";
            var children = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _startupBySection[field.Key.Split(':')[0]])
            {
                if (entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    children[entry.Key[prefix.Length..]] = entry.Value;
                }
            }
            values[field.Key] = new(_startup.GetValueOrDefault(field.Key), children, Node(_lastValidDocument, field.Key)?.DeepClone())
            {
                NextStartText = NextStart(field.Key),
                NextStartItems = NextStartItems(field.Key)
            };
        }
        return values;
    }

    // Names only: an unmanaged key may hold anything, including a secret.
    private List<string> UnknownFileKeys()
    {
        var unknown = new List<string>();
        if (_lastValidDocument is { } document)
        {
            JsonObject onDisk;
            try { onDisk = CleanConfigurationForDisk(document); }
            catch (Exception exception) when (IsStorageError(exception)) { onDisk = document; }
            Visit(onDisk, "");
        }
        return unknown;

        void Visit(JsonObject parent, string path)
        {
            foreach (var (name, node) in parent)
            {
                var child = path.Length == 0 ? name : path + ":" + name;
                if (node is JsonObject nested)
                {
                    Visit(nested, child);
                }
                else if (!PrivateMetadata(child) && !ConfigurationCatalog.Knows(child))
                {
                    unknown.Add(child);
                }
            }
        }
    }

    // A live module's candidate: the next start's file with every restart-only field
    // put back to the value this process is running with.
    private JsonObject Running(JsonObject nextStart)
    {
        var running = (JsonObject)nextStart.DeepClone();
        foreach (var field in ConfigurationCatalog.RestartFields)
        {
            SetField(running, field.Key, Node(_startupDocument, field.Key)?.DeepClone());
        }
        return running;
    }

    private static JsonNode? Node(JsonObject? document, string key)
    {
        JsonNode? current = document;
        foreach (var segment in key.Split(':'))
        {
            if (current is not JsonObject parent)
            {
                return null;
            }
            var name = parent.Select(item => item.Key).FirstOrDefault(name => name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return null;
            }
            current = parent[name];
        }
        return current;
    }

    // The section a key is persisted under: a model connection keeps the section the deployment resolved.
    public string PhysicalKey(string key) => _modelPaths.PhysicalKey(key);

    // The runtime retains its startup layout for in-flight and subsequent edits. Normalize only the disk copy.
    private JsonObject CleanConfigurationForDisk(JsonObject current)
    {
        var disk = (JsonObject)current.DeepClone();
        foreach (var key in new[] { "Enrichment:FieldsEnabled", "Enrichment:AudioEnabled", "Enrichment:Tts:Provider", "Models:Speech:Provider",
                     "VocabularyPhotoImport:Enabled", "VocabularyPhotoImport:BaseUrl", "VocabularyPhotoImport:ApiKey",
                     "VocabularyPhotoImport:RecognitionBudgetSeconds", "VocabularyPhotoImport:MaxResponseBytes",
                     "AdminAuthentication:Oidc", "AdminAuthentication:Gateway", "AdminAuthentication:CookieName", "AdminAuthentication:CookieSecure",
                     "Audio:MaxTextbookUploadBytes", "Audio:MaxTextbookDurationSeconds",
                     "Telemetry:Otlp:Traces:AllowInsecureLoopbackForTesting", "Telemetry:Otlp:Metrics:AllowInsecureLoopbackForTesting" })
            while (RemoveField(disk, key, out _, includeDescendants: true)) { }

        Normalize(ModelConfigurationPaths.OldText, ModelConfigurationPaths.ReasoningKey, _modelPaths.Reasoning,
            [ModelConfigurationPaths.OldText, ModelConfigurationPaths.OldReview]);
        Normalize(ModelConfigurationPaths.OldSpeech, ModelConfigurationPaths.SpeechKey, _modelPaths.Speech,
            [ModelConfigurationPaths.OldSpeech]);
        return disk;

        void Normalize(string logical, string canonical, string active, string[] oldSections)
        {
            var fields = EnrichmentConfigurationFields.All.Where(f => f.Key.StartsWith(logical + ":", StringComparison.Ordinal)).ToArray();
            // Preserve the underlying file connections while deployment overrides select a group.
            if (fields.Any(f => !_sources[f.Key].Editable)) return;
            foreach (var field in fields)
            {
                var suffix = field.Key[logical.Length..];
                foreach (var old in oldSections)
                    if (RemoveField(disk, old + suffix, out var value) && old == active)
                        SetField(disk, canonical + suffix, value);
            }
        }
    }

    private static bool RemoveField(JsonObject node, string path, out JsonNode? value, bool includeDescendants = false)
    {
        foreach (var name in node.Select(item => item.Key).ToArray())
        {
            if (path.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                includeDescendants && name.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase))
                return node.Remove(name, out value);
            if (path.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) && node[name] is JsonObject child
                && RemoveField(child, path[(name.Length + 1)..], out value, includeDescendants))
            {
                if (child.Count == 0) node.Remove(name);
                return true;
            }
        }
        value = null;
        return false;
    }

    private static void SetField(JsonObject document, string key, JsonNode? value)
    {
        // .NET also accepts colon-delimited property names; update their semantic
        // location without creating a duplicate flattened key on the next startup.
        var name = document.Select(item => item.Key).FirstOrDefault(name => key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            Assign(document, name, value);
            return;
        }
        name = document.Select(item => item.Key)
            .FirstOrDefault(name => key.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            var child = document[name] as JsonObject ?? throw new IOException(ConfigurationSaveResult.StorageFailureMessage);
            SetField(child, key[(name.Length + 1)..], value);
            // A section that a removal left empty goes with it.
            if (value is null && child.Count == 0) document.Remove(name);
            return;
        }
        var separator = key.IndexOf(':');
        if (separator < 0)
        {
            Assign(document, key, value);
            return;
        }
        if (value is null)
        {
            // Nothing was persisted to remove.
            return;
        }
        var section = new JsonObject();
        document[key[..separator]] = section;
        SetField(section, key[(separator + 1)..], value);
    }

    // A null value removes a persisted key, so the next start falls back to the default.
    private static void Assign(JsonObject document, string name, JsonNode? value)
    {
        if (value is null)
        {
            document.Remove(name);
        }
        else
        {
            document[name] = value;
        }
    }

    private static bool PrivateMetadata(string key) => key.Equals("EnrichmentConfigurationVersions", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("EnrichmentConfigurationVersions:", StringComparison.OrdinalIgnoreCase);

    private static bool ValidField(string key, JsonNode? value) => EnrichmentConfigurationFields.Find(key) is { } enrichment
        ? enrichment.Valid(value)
        : ConfigurationCatalog.Find(key) is { Apply: ConfigurationApply.Restart } restart ? restart.Valid(value)
        : value is JsonValue scalar
        && (key.EndsWith(":Enabled", StringComparison.OrdinalIgnoreCase) ? scalar.TryGetValue<bool>(out _) : scalar.TryGetValue<string>(out _));
    private static bool IsStorageError(Exception exception) => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static string NewRevision() => $"\"{Guid.NewGuid():N}\"";
    private static bool ValidRevision(string value) => value.Length == 34 && value[0] == '"' && value[^1] == '"'
        && Guid.TryParseExact(value[1..^1], "N", out _);
}
