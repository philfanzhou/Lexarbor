using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Host.Integration;

namespace Lexarbor.Host.Configuration;

internal enum EnrichmentFieldKind { String, Boolean, Integer, Speed }
internal sealed record EnrichmentConfigurationField(string Name, string Key, EnrichmentFieldKind Kind,
    object? Default, decimal Minimum = 0, decimal Maximum = 0, bool Secret = false)
{
    public bool Valid(JsonNode? value) => Kind switch
    {
        EnrichmentFieldKind.Speed => value is null || value is JsonValue speed && speed.TryGetValue<decimal>(out var n) && n >= Minimum && n <= Maximum,
        EnrichmentFieldKind.Boolean => value is JsonValue boolean && boolean.TryGetValue<bool>(out _),
        EnrichmentFieldKind.Integer => value is JsonValue integer && integer.TryGetValue<int>(out var n) && n >= Minimum && n <= Maximum,
        _ => value is JsonValue text && text.TryGetValue<string>(out var s) && s.Length <= (Secret ? 4096 : 2048) && (!Secret || !s.Any(char.IsControl))
    };
}

internal static class EnrichmentConfigurationFields
{
    public static readonly IReadOnlyList<EnrichmentConfigurationField> All = Build();
    public static EnrichmentConfigurationField? Find(string key) => All.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    private static List<EnrichmentConfigurationField> Build()
    {
        var fields = new List<EnrichmentConfigurationField>();
        void Add(string name, string key, EnrichmentFieldKind kind, object? value, decimal min = 0, decimal max = 0, bool secret = false)
            => fields.Add(new(name, key, kind, value, min, max, secret));
        void Client(string name, string key)
        {
            Add(name + ".endpoint", key + ":Endpoint", EnrichmentFieldKind.String, "");
            Add(name + ".model", key + ":Model", EnrichmentFieldKind.String, "");
            Add(name + ".apiKey", key + ":ApiKey", EnrichmentFieldKind.String, "", secret: true);
            Add(name + ".timeoutSeconds", key + ":TimeoutSeconds", EnrichmentFieldKind.Integer, 30, 1, 300);
        }
        Client("reviewAssistant", OpenAiCompatibleReviewAssistant.Section);
        Client("enrichment.text", "Enrichment:Text");
        Add("enrichment.fieldsEnabled", "Enrichment:FieldsEnabled", EnrichmentFieldKind.Boolean, false);
        Add("enrichment.audioEnabled", "Enrichment:AudioEnabled", EnrichmentFieldKind.Boolean, false);
        Add("enrichment.tts.provider", "Enrichment:Tts:Provider", EnrichmentFieldKind.String, "MiniMaxNative");
        Add("enrichment.tts.endpoint", "Enrichment:Tts:Endpoint", EnrichmentFieldKind.String, "");
        Add("enrichment.tts.model", "Enrichment:Tts:Model", EnrichmentFieldKind.String, "speech-2.8-hd");
        Add("enrichment.tts.apiKey", "Enrichment:Tts:ApiKey", EnrichmentFieldKind.String, "", secret: true);
        Add("enrichment.tts.timeoutSeconds", "Enrichment:Tts:TimeoutSeconds", EnrichmentFieldKind.Integer, 30, 1, 300);
        foreach (var locale in new[] { "Uk", "Us" })
        {
            var name = "enrichment.tts." + locale.ToLowerInvariant();
            Add(name + ".voice", "Enrichment:Tts:" + locale + ":Voice", EnrichmentFieldKind.String, "");
            Add(name + ".speed", "Enrichment:Tts:" + locale + ":Speed", EnrichmentFieldKind.Speed, null, 0.1m, 4m);
        }
        foreach (var setting in new[] {
            ("concurrency", "Concurrency", 1, 1, 8), ("failureBudget", "FailureBudget", 5, 1, 20),
            ("serviceFailureThreshold", "ServiceFailureThreshold", 5, 1, 20),
            ("servicePauseSeconds", "ServicePauseSeconds", 60, 1, 600),
            ("maxServicePauseSeconds", "MaxServicePauseSeconds", 600, 1, 3600),
            ("throttleInitialSeconds", "ThrottleInitialSeconds", 8, 1, 120),
            ("throttleMaxSeconds", "ThrottleMaxSeconds", 120, 1, 3600),
            ("discoveryBatchSize", "DiscoveryBatchSize", 20, 1, 1000),
            ("scanIntervalSeconds", "ScanIntervalSeconds", 30, 1, 3600) })
            Add("enrichment." + setting.Item1, "Enrichment:" + setting.Item2, EnrichmentFieldKind.Integer, setting.Item3, setting.Item4, setting.Item5);
        return fields;
    }

    public static Dictionary<string, JsonNode?> Read(IConfiguration configuration)
    {
        var fields = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in All)
        {
            var text = configuration[field.Key];
            JsonNode? value = text is null ? JsonSerializer.SerializeToNode(field.Default) : field.Kind switch
            {
                EnrichmentFieldKind.Boolean when bool.TryParse(text, out var boolean) => JsonValue.Create(boolean),
                EnrichmentFieldKind.Integer when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => JsonValue.Create(integer),
                EnrichmentFieldKind.Speed when string.IsNullOrEmpty(text) => null,
                EnrichmentFieldKind.Speed when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var speed) => JsonValue.Create(decimal.Parse(speed.ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
                EnrichmentFieldKind.String => JsonValue.Create(field.Secret ? text : text.Trim()),
                _ => throw new InvalidOperationException("Invalid enrichment settings.")
            };
            if (!field.Valid(value)) throw new InvalidOperationException("Invalid enrichment settings.");
            if (field.Key.EndsWith(":Endpoint", StringComparison.Ordinal) && value!.GetValue<string>() is { Length: > 0 } endpoint
                && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                    || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
                throw new InvalidOperationException("Invalid enrichment settings.");
            fields.Add(field.Key, value);
        }
        if (fields["Enrichment:Tts:Provider"]!.GetValue<string>() is not ("MiniMaxNative" or "OpenAICompatible")
            || fields["Enrichment:MaxServicePauseSeconds"]!.GetValue<int>() < fields["Enrichment:ServicePauseSeconds"]!.GetValue<int>()
            || fields["Enrichment:ThrottleMaxSeconds"]!.GetValue<int>() < fields["Enrichment:ThrottleInitialSeconds"]!.GetValue<int>())
            throw new InvalidOperationException("Invalid enrichment settings.");
        return fields;
    }
}

/// <summary>Immutable operation input. It is server-only and must never be serialized or logged.</summary>
public sealed class EnrichmentClientSettings(string endpoint, string model, string apiKey, int timeoutSeconds)
{
    public string Endpoint { get; } = endpoint;
    public string Model { get; } = model;
    public string ApiKey { get; } = apiKey;
    public int TimeoutSeconds { get; } = timeoutSeconds;
    public bool Configured => Endpoint.Length > 0 && Model.Length > 0 && !string.IsNullOrWhiteSpace(ApiKey);
}
public sealed record EnrichmentVoiceSettings(string Voice, decimal? Speed);
public sealed class EnrichmentSettings
{
    internal EnrichmentSettings(IReadOnlyDictionary<string, JsonNode?> fields)
    {
        string S(string key) => fields["Enrichment:" + key]!.GetValue<string>();
        int I(string key) => fields["Enrichment:" + key]!.GetValue<int>();
        FieldsEnabled = fields["Enrichment:FieldsEnabled"]!.GetValue<bool>();
        AudioEnabled = fields["Enrichment:AudioEnabled"]!.GetValue<bool>();
        Text = new(S("Text:Endpoint"), S("Text:Model"), S("Text:ApiKey"), I("Text:TimeoutSeconds"));
        Tts = new(S("Tts:Endpoint"), S("Tts:Model"), S("Tts:ApiKey"), I("Tts:TimeoutSeconds"));
        Provider = S("Tts:Provider");
        Uk = new(S("Tts:Uk:Voice"), fields["Enrichment:Tts:Uk:Speed"]?.GetValue<decimal>());
        Us = new(S("Tts:Us:Voice"), fields["Enrichment:Tts:Us:Speed"]?.GetValue<decimal>());
        Concurrency = I("Concurrency"); FailureBudget = I("FailureBudget");
        ServiceFailureThreshold = I("ServiceFailureThreshold"); ServicePauseSeconds = I("ServicePauseSeconds");
        MaxServicePauseSeconds = I("MaxServicePauseSeconds"); ThrottleInitialSeconds = I("ThrottleInitialSeconds");
        ThrottleMaxSeconds = I("ThrottleMaxSeconds"); DiscoveryBatchSize = I("DiscoveryBatchSize"); ScanIntervalSeconds = I("ScanIntervalSeconds");
    }
    public bool FieldsEnabled { get; }
    public bool AudioEnabled { get; }
    public EnrichmentClientSettings Text { get; }
    public EnrichmentClientSettings Tts { get; }
    public string Provider { get; }
    public EnrichmentVoiceSettings Uk { get; }
    public EnrichmentVoiceSettings Us { get; }
    public int Concurrency { get; }
    public int FailureBudget { get; }
    public int ServiceFailureThreshold { get; }
    public int ServicePauseSeconds { get; }
    public int MaxServicePauseSeconds { get; }
    public int ThrottleInitialSeconds { get; }
    public int ThrottleMaxSeconds { get; }
    public int DiscoveryBatchSize { get; }
    public int ScanIntervalSeconds { get; }
}
public sealed class EnrichmentConfigurationSnapshot
{
    internal EnrichmentConfigurationSnapshot(ReviewAssistantOptions review, Dictionary<string, JsonNode?> fields, string? text, string? uk, string? us, string? ttsService)
    { ReviewAssistant = review; Fields = fields; Enrichment = new(fields); TextVersion = text; UkTtsVersion = uk; UsTtsVersion = us; TtsServiceVersion = ttsService; }
    internal IReadOnlyDictionary<string, JsonNode?> Fields { get; }
    public ReviewAssistantOptions ReviewAssistant { get; }
    public EnrichmentSettings Enrichment { get; }
    public string? TextVersion { get; }
    public string? UkTtsVersion { get; }
    public string? UsTtsVersion { get; }
    public string? TtsServiceVersion { get; }
    public bool ConfigurationReady => TextVersion is not null && UkTtsVersion is not null && UsTtsVersion is not null && TtsServiceVersion is not null;
    public bool CanPrepare => ConfigurationReady && (Enrichment.FieldsEnabled || Enrichment.AudioEnabled) && Enrichment.Text.Configured;
    public bool HasTtsConfiguration(string locale) => CanPrepare && Enrichment.AudioEnabled && Enrichment.Tts.Configured
        && (locale == "uk" ? Enrichment.Uk : locale == "us" ? Enrichment.Us : null) is { Voice.Length: > 0, Speed: not null };
}

/// <summary>One publication swaps review and enrichment inputs after their file commit.</summary>
public sealed class EnrichmentRuntimeConfiguration
{
    private const string Metadata = "EnrichmentConfigurationVersions";
    private readonly object _gate = new();
    private EnrichmentConfigurationSnapshot _current;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public EnrichmentRuntimeConfiguration(IConfiguration configuration, PersistentConfigurationFileStore store)
    {
        // Private version metadata is authoritative only in the persisted document.
        // Environment/CLI values can configure services, never impersonate durability.
        var document = store.Read().Document ?? new JsonObject();
        var persisted = new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document))).Build();
        using var lifetime = persisted as IDisposable;
        _current = Create(configuration, false, persisted).Snapshot;
    }
    public EnrichmentConfigurationSnapshot Current => Volatile.Read(ref _current);
    // Run before hosted workers start. Disabled legacy deployments and GET remain
    // read-only; enabled deployments establish durable versions without a UI step.
    internal async Task InitializeAsync(PersistentConfigurationCoordinator coordinator, CancellationToken cancellationToken = default)
    {
        var current = Current;
        if (current.ConfigurationReady || !current.Enrichment.FieldsEnabled && !current.Enrichment.AudioEnabled) return;
        var read = await coordinator.ReadAsync(cancellationToken);
        var fields = current.Fields.Where(f => read.Sources[f.Key].Editable)
            .ToDictionary(f => f.Key, f => f.Value?.DeepClone(), StringComparer.OrdinalIgnoreCase);
        await coordinator.SaveAsync(read.Revision, fields, PrepareAsync, cancellationToken, allowEmptyFields: true);
        // Storage failures deliberately retain unready versions and safe waiting.
    }

    public Task<PreparedConfigurationChange?> PrepareAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = Create(configuration, true);
        return Task.FromResult<PreparedConfigurationChange?>(new(() => Publish(candidate.Snapshot), enrichmentVersions: candidate.Metadata));
    }
    public async Task<EnrichmentConfigurationSnapshot> WaitForChangeAsync(EnrichmentConfigurationSnapshot observed, CancellationToken cancellationToken)
    {
        Task changed;
        lock (_gate) { if (!ReferenceEquals(_current, observed)) return _current; changed = _changed.Task; }
        await changed.WaitAsync(cancellationToken);
        return Current;
    }
    private void Publish(EnrichmentConfigurationSnapshot candidate)
    {
        TaskCompletionSource changed;
        lock (_gate) { Volatile.Write(ref _current, candidate); changed = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        changed.TrySetResult();
    }
    private static (EnrichmentConfigurationSnapshot Snapshot, JsonObject? Metadata) Create(IConfiguration configuration, bool persist, IConfiguration? persisted = null)
    {
        var fields = EnrichmentConfigurationFields.Read(configuration);
        var review = ReviewAssistantOptions.Read(configuration);
        var versions = persisted ?? configuration;
        byte[]? key = null;
        try { key = Convert.FromBase64String(versions[Metadata + ":Key"] ?? ""); } catch (FormatException) { }
        if (key?.Length != 32) key = persist ? RandomNumberGenerator.GetBytes(32) : null;
        var metadata = key is null ? null : new JsonObject { ["Key"] = Convert.ToBase64String(key) };
        string? Version(string name, Func<string, bool> include)
        {
            if (key is null) return null;
            var selected = fields.Where(f => include(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal).ToDictionary(f => f.Key, f => f.Value);
            var digest = Convert.ToBase64String(HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(selected)));
            var oldDigest = versions[$"{Metadata}:{name}:Digest"];
            var version = versions[$"{Metadata}:{name}:Version"];
            var matches = oldDigest == digest && Guid.TryParseExact(version, "N", out _);
            version = matches ? version : persist ? Guid.NewGuid().ToString("N") : null;
            metadata![name] = new JsonObject { ["Digest"] = digest, ["Version"] = version };
            return version;
        }
        var text = Version("Text", fieldKey => fieldKey.StartsWith("Enrichment:Text:", StringComparison.Ordinal));
        string? Tts(string locale) => Version(locale, fieldKey => fieldKey.StartsWith("Enrichment:Tts:", StringComparison.Ordinal)
            && (!fieldKey.StartsWith("Enrichment:Tts:Uk:", StringComparison.Ordinal) && !fieldKey.StartsWith("Enrichment:Tts:Us:", StringComparison.Ordinal)
                || fieldKey.StartsWith("Enrichment:Tts:" + locale + ":", StringComparison.Ordinal)));
        var uk = Tts("Uk"); var us = Tts("Us");
        var ttsService = Version("TtsService", fieldKey => fieldKey.StartsWith("Enrichment:Tts:", StringComparison.Ordinal)
            && !fieldKey.StartsWith("Enrichment:Tts:Uk:", StringComparison.Ordinal) && !fieldKey.StartsWith("Enrichment:Tts:Us:", StringComparison.Ordinal));
        return (new(review, fields, text, uk, us, ttsService), persist ? metadata : null);
    }
}
