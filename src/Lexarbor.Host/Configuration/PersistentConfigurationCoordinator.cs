using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Lexarbor.Host.Configuration;

public enum ConfigurationValueSource { Default, File, Environment, CommandLine }
public enum ConfigurationSaveStatus { Success, MissingRevision, InvalidRevision, Conflict, Overridden, InvalidCandidate, StorageFailure, ResourceConflict }
public sealed record ConfigurationFieldSource(ConfigurationValueSource Source, bool Editable);
public sealed record ConfigurationReadResult(string Revision, IReadOnlyDictionary<string, ConfigurationFieldSource> Sources, bool StorageAvailable);
public sealed record ConfigurationSaveResult(ConfigurationSaveStatus Status, string? Revision = null, IReadOnlyList<string>? OverriddenFields = null)
{
    public const string StorageFailureMessage = "Persistent configuration is unavailable.";
}

public sealed class ConfigurationChangeConflictException() : Exception("Existing audio requires a separate storage migration.");

/// <summary>The module owns validation and supplies a non-throwing, synchronous publication.</summary>
public sealed class PreparedConfigurationChange(Action publish, Action? discard = null) : IDisposable
{
    private bool _published;

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
    private static readonly HashSet<string> AllowedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "RateLimits:AdminLogin:Enabled", "RateLimits:PublicApi:Enabled",
        "Audio:Enabled", "Audio:InternalEndpoint", "Audio:PublicEndpoint", "Audio:Region",
        "Audio:Bucket", "Audio:KeyPrefix", "Audio:AccessKey", "Audio:SecretKey"
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IConfigurationRoot _configuration;
    private readonly PersistentConfigurationFileStore _store;
    private readonly Dictionary<string, string?> _startup;
    private readonly Dictionary<string, string?> _overrides;
    private Dictionary<string, ConfigurationFieldSource> _sources;
    private JsonObject? _lastValidDocument;
    private byte[]? _diskBytes;
    private string _revision = NewRevision();
    // A disk edit is not a runtime edit. Reject writes until the host restarts,
    // rather than merging a new file underneath startup-only host settings.
    private bool _externalChange;

    public PersistentConfigurationCoordinator(IConfigurationRoot configuration, PersistentConfigurationFileStore store)
    {
        _configuration = configuration;
        _store = store;
        _startup = configuration.AsEnumerable().ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        _overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _sources = new Dictionary<string, ConfigurationFieldSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in AllowedFields)
        {
            var source = FindSource(field);
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
        var snapshot = store.Read();
        _lastValidDocument = snapshot.Document;
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
            return read(new ConfigurationReadResult(_revision,
                new Dictionary<string, ConfigurationFieldSource>(_sources, StringComparer.OrdinalIgnoreCase), available));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConfigurationSaveResult> SaveAsync(
        string? revision,
        IReadOnlyDictionary<string, JsonNode?> fields,
        Func<IConfiguration, CancellationToken, Task<PreparedConfigurationChange?>> prepare,
        CancellationToken cancellationToken = default)
    {
        if (revision is null)
        {
            return new(ConfigurationSaveStatus.MissingRevision);
        }
        if (!ValidRevision(revision))
        {
            return new(ConfigurationSaveStatus.InvalidRevision);
        }
        if (fields.Count == 0 || fields.Keys.Any(key => !AllowedFields.Contains(key))
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
                    SetField(document, field.Key, field.Value?.DeepClone());
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
                // Overlay every file field, including other modules' successful saves.
                values.AddJsonStream(new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document)));
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
                var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var nextRevision = NewRevision();
                var nextSources = new Dictionary<string, ConfigurationFieldSource>(_sources, StringComparer.OrdinalIgnoreCase);
                foreach (var field in fields.Keys)
                {
                    nextSources[field] = new(ConfigurationValueSource.File, true);
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
                var file = json.Source.FileProvider?.GetFileInfo(json.Source.Path!);
                return new(file?.PhysicalPath == _store.FilePath ? ConfigurationValueSource.File : ConfigurationValueSource.Default, true);
            }
            return new(ConfigurationValueSource.Default, true);
        }
        return new(ConfigurationValueSource.Default, true);
    }

    private static void SetField(JsonObject document, string key, JsonNode? value)
    {
        // .NET also accepts colon-delimited property names; update their semantic
        // location without creating a duplicate flattened key on the next startup.
        var name = document.Select(item => item.Key).FirstOrDefault(name => key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            document[name] = value;
            return;
        }
        name = document.Select(item => item.Key)
            .FirstOrDefault(name => key.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            var child = document[name] as JsonObject ?? throw new IOException(ConfigurationSaveResult.StorageFailureMessage);
            SetField(child, key[(name.Length + 1)..], value);
            return;
        }
        var separator = key.IndexOf(':');
        if (separator < 0)
        {
            document[key] = value;
            return;
        }
        var section = new JsonObject();
        document[key[..separator]] = section;
        SetField(section, key[(separator + 1)..], value);
    }

    private static bool ValidField(string key, JsonNode? value) => value is JsonValue scalar
        && (key.EndsWith(":Enabled", StringComparison.OrdinalIgnoreCase) ? scalar.TryGetValue<bool>(out _) : scalar.TryGetValue<string>(out _));
    private static bool IsStorageError(Exception exception) => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static string NewRevision() => $"\"{Guid.NewGuid():N}\"";
    private static bool ValidRevision(string value) => value.Length == 34 && value[0] == '"' && value[^1] == '"'
        && Guid.TryParseExact(value[1..^1], "N", out _);
}
