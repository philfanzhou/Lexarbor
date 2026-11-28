using System.Globalization;
using System.Text.Json.Nodes;

namespace Lexarbor.Host.Configuration;

/// <summary>How a configuration value is validated and edited.</summary>
internal enum ConfigurationValueKind { Text, Url, Boolean, TriState, Integer, Number, Secret, List, IntegerList, Select }

/// <summary>How a configuration key reaches the running process.</summary>
internal enum ConfigurationApply
{
    /// <summary>Its settings module publishes it as soon as the file commit succeeds.</summary>
    Live,
    /// <summary>Read once at startup; a saved value applies at the next start.</summary>
    Restart,
    /// <summary>Managed by the deployment: the environment, files on disk, or the process itself.</summary>
    ReadOnly
}

/// <summary>One configuration key Lexarbor reads: how it is displayed, validated and applied.</summary>
internal sealed record ConfigurationCatalogField
{
    public required string Key { get; init; }
    public required string Group { get; init; }
    public required string Section { get; init; }
    public required string Name { get; init; }
    public required ConfigurationValueKind Kind { get; init; }
    public required ConfigurationApply Apply { get; init; }
    public string? Description { get; init; }
    /// <summary>The value in effect when the key is absent, in configuration text form; null when there is none.</summary>
    public string? Default { get; init; }
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public bool Nullable { get; init; }
    /// <summary>Why a read-only key cannot be changed from the administration surface.</summary>
    public string? Reason { get; init; }
    /// <summary>The settings module that publishes a live key, and the key's name in that module's request body.</summary>
    public string? Module { get; init; }
    public string? Wire { get; init; }
    /// <summary>
    /// The key the module and the persisted file may still hold a live model connection under, when
    /// it differs from the displayed one: an upgrade keeps reading a connection under its old section.
    /// </summary>
    public string? ModuleKey { get; init; }
    /// <summary>The key names a process environment variable rather than a configuration entry.</summary>
    public bool EnvironmentVariable { get; init; }

    public bool Secret => Kind == ConfigurationValueKind.Secret;
    public bool Items => Kind is ConfigurationValueKind.List or ConfigurationValueKind.IntegerList;
    /// <summary>The key the coordinator keeps this field's source under.</summary>
    public string SourceKey => ModuleKey ?? Key;

    /// <summary>
    /// The values a restart-only key accepts from a settings request. A null removes the key from
    /// the file, so the next start falls back to the default; numbers and booleans always carry a
    /// value. Candidate-level rules live in <see cref="StartupConfigurationValidator"/>.
    /// </summary>
    public bool Valid(JsonNode? value) => Kind switch
    {
        ConfigurationValueKind.List => value is JsonArray texts && texts.Count <= MaximumItems
            && texts.All(item => item is JsonValue text && text.TryGetValue<string>(out var content) && content.Length is > 0 and <= 256),
        // A persisted file cannot hold an empty array, which the next start would read as the default.
        ConfigurationValueKind.IntegerList => value is JsonArray numbers && numbers.Count is >= 1 and <= MaximumItems
            && numbers.All(item => IsNumber(item, out var amount) && amount == Math.Truncate(amount) && InBounds(amount)),
        ConfigurationValueKind.Secret => ValidText(value, 4096) && !value!.GetValue<string>().Any(char.IsControl),
        ConfigurationValueKind.Integer => IsNumber(value, out var whole) && whole == Math.Truncate(whole) && InBounds(whole),
        ConfigurationValueKind.Number => IsNumber(value, out var exact) && InBounds(exact),
        ConfigurationValueKind.Boolean => value is JsonValue { } flag && flag.TryGetValue<bool>(out _),
        ConfigurationValueKind.TriState => value is null || value is JsonValue { } state && state.TryGetValue<bool>(out _),
        ConfigurationValueKind.Text or ConfigurationValueKind.Url or ConfigurationValueKind.Select =>
            value is null || ValidText(value, 2048) && !value!.GetValue<string>().Any(char.IsControl),
        _ => false
    };

    private const int MaximumItems = 64;
    private bool InBounds(decimal number) => number >= (Minimum ?? decimal.MinValue) && number <= (Maximum ?? decimal.MaxValue);

    // A JSON number, whichever way the node came to be: parsed from a request, or built in memory.
    private static bool IsNumber(JsonNode? node, out decimal number)
    {
        number = 0;
        return node is JsonValue && decimal.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool ValidText(JsonNode? value, int maximum) => value is JsonValue text
        && text.TryGetValue<string>(out var content) && content.Length <= maximum;
}

internal sealed record ConfigurationCatalogGroup(string Id, string Area, string Name, string? Description);
internal sealed record ConfigurationCatalogSection(string Group, string Id, string Name);

/// <summary>
/// Every configuration key Lexarbor reads, in display order. A key missing here can still be
/// configured, but the administration surface neither shows nor changes it.
/// </summary>
internal static class ConfigurationCatalog
{
    public const string DeploymentOverrideReason = "Overridden by deployment configuration.";
    public const string LogLevels = "Trace,Debug,Information,Warning,Error,Critical,None";
    public const string OtlpProtocols = "Grpc,HttpProtobuf";
    public const string ConnectionString = "ConnectionStrings:Default";

    public static readonly IReadOnlyList<ConfigurationCatalogField> Fields = Build();
    public static readonly IReadOnlyList<ConfigurationCatalogGroup> Groups = BuildGroups();
    public static readonly IReadOnlyList<ConfigurationCatalogSection> Sections = BuildSections();
    public static IReadOnlyList<ConfigurationCatalogField> RestartFields { get; } =
        Fields.Where(field => field.Apply == ConfigurationApply.Restart).ToArray();

    public static ConfigurationCatalogField? Find(string key) =>
        Fields.FirstOrDefault(field => field.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a path in the persisted file is one this release reads: a catalog key, a key under
    /// one, or a model connection under the section an upgrade still reads it from.
    /// </summary>
    public static bool Knows(string path) => Fields.Any(field => Matches(field.Key, path) || field.ModuleKey is { } module && Matches(module, path))
        || LegacyModelKeys.Any(key => Matches(key, path));

    private static bool Matches(string key, string path) => path.Equals(key, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase);

    // A reasoning connection configured before the model sections existed, under the review assistant's own section.
    private static readonly string[] LegacyModelKeys =
        ["MeaningSubmissions:Ai:Endpoint", "MeaningSubmissions:Ai:Model", "MeaningSubmissions:Ai:ApiKey", "MeaningSubmissions:Ai:TimeoutSeconds"];

    private static string[] Split(string value) => value.Split(',');

    private static List<ConfigurationCatalogField> Build()
    {
        var fields = new List<ConfigurationCatalogField>();
        void Add(string key, string group, string section, string name, ConfigurationValueKind kind,
            ConfigurationApply apply, string? description = null, string? @default = null,
            decimal? minimum = null, decimal? maximum = null, IReadOnlyList<string>? options = null, string? reason = null,
            bool nullable = false)
            => fields.Add(new()
            {
                Key = key,
                Group = group,
                Section = section,
                Name = name,
                Kind = kind,
                Apply = apply,
                Description = description,
                Default = @default,
                Minimum = minimum,
                Maximum = maximum,
                Options = options,
                Reason = reason,
                Nullable = nullable
            });
        void Live(string key, string group, string section, string name, ConfigurationValueKind kind, string module, string wire,
            string? description = null, string? @default = null)
            => fields.Add(new()
            {
                Key = key,
                Group = group,
                Section = section,
                Name = name,
                Kind = kind,
                Apply = ConfigurationApply.Live,
                Description = description,
                Default = @default,
                Module = module,
                Wire = wire
            });

        // Sign-in and identity
        Add("IdentityService:Authority", "identity", "provider", "Identity provider authority", ConfigurationValueKind.Url,
            ConfigurationApply.Restart, "Base address of the identity provider whose Discovery document and signing keys are trusted.",
            "http://localhost:8080");
        Add("IdentityService:Issuer", "identity", "provider", "Token issuer", ConfigurationValueKind.Text, ConfigurationApply.Restart,
            "The exact iss value an access token must carry.", "http://localhost:8080");
        Add("IdentityService:Audience", "identity", "provider", "Token audience", ConfigurationValueKind.Text, ConfigurationApply.Restart,
            "The exact aud value an access token must carry.", "lexarbor");
        Add("IdentityService:RequireHttpsMetadata", "identity", "provider", "Require HTTPS metadata", ConfigurationValueKind.TriState,
            ConfigurationApply.Restart, "Unset: required outside Development and Testing unless the authority is a loopback address.");
        Add("AdminAuthentication:OidcCode:ClientId", "identity", "hosted-login", "Hosted login client ID", ConfigurationValueKind.Text,
            ConfigurationApply.Restart, "The application ID registered with the identity provider.", "");
        Add("AdminAuthentication:OidcCode:ClientSecret", "identity", "hosted-login", "Hosted login client secret",
            ConfigurationValueKind.Secret, ConfigurationApply.Restart, "The application secret; it never reaches the browser.", "");
        Add("AdminAuthentication:OidcCode:RedirectUri", "identity", "hosted-login", "Hosted login redirect URI", ConfigurationValueKind.Url,
            ConfigurationApply.Restart, "The exact registered external URI; its path must be /admin/auth/callback.", "");
        Add("AdminAuthentication:OidcCode:PostLogoutRedirectUri", "identity", "hosted-login", "Post-logout redirect URI",
            ConfigurationValueKind.Url, ConfigurationApply.Restart,
            "Optional; when set, its path must be /admin/auth/logout/return.", "");
        Add("AdminAuthentication:OidcCode:Scope", "identity", "hosted-login", "Hosted login scope", ConfigurationValueKind.Text,
            ConfigurationApply.Restart, "Space-separated: openid, with optional profile.", "openid profile");
        Add("AdminAuthentication:RequiredRole", "identity", "access", "Administrator role", ConfigurationValueKind.Text,
            ConfigurationApply.Restart, "The role an access token must carry to administer Lexarbor.", "admin");
        Add("AdminAuthentication:Provider", "identity", "access", "Sign-in provider (removed)", ConfigurationValueKind.Text,
            ConfigurationApply.ReadOnly, "Retained only as a tripwire: it must stay unset or exactly OidcCode.", "",
            reason: "Removed. Hosted login is the only sign-in, and this key no longer selects one.");
        Add("AdminAuthentication:HttpTestOrigins", "identity", "access", "HTTP test origins (removed)", ConfigurationValueKind.Text,
            ConfigurationApply.ReadOnly, "Retained only as a tripwire: any value stops startup.", "",
            reason: "Removed. The plain-HTTP test transport no longer exists, and this key must stay unset.");

        // Request rate limits: the switch is live, the numbers are read at startup.
        Live("RateLimits:AdminLogin:Enabled", "rate-limits", "admin-login", "Administrator login rate limit",
            ConfigurationValueKind.Boolean, "rateLimits", "adminLoginEnabled", "Shared by every request from one client address.", "false");
        Add("RateLimits:AdminLogin:PermitLimit", "rate-limits", "admin-login", "Administrator login permit limit",
            ConfigurationValueKind.Integer, ConfigurationApply.Restart, "Requests per window for /admin/auth/start.", "10", 1, 10000);
        Add("RateLimits:AdminLogin:WindowSeconds", "rate-limits", "admin-login", "Administrator login window (seconds)",
            ConfigurationValueKind.Integer, ConfigurationApply.Restart, null, "300", 10, 600);

        // Network
        Add("Network:TrustedProxies", "network", "forwarding", "Trusted proxies", ConfigurationValueKind.List,
            ConfigurationApply.Restart, "Upstream proxy addresses allowed to state the client address, one per entry.", maximum: 64);
        Add("Network:TrustedNetworks", "network", "forwarding", "Trusted networks", ConfigurationValueKind.List,
            ConfigurationApply.Restart, "Upstream proxy ranges in CIDR form, one per entry.", maximum: 64);
        Add("Network:ForwardLimit", "network", "forwarding", "Forwarded-header hop limit", ConfigurationValueKind.Integer,
            ConfigurationApply.Restart, "How many trusted hops to walk back; it must equal the real hop count.", "1", 1, 10);

        // Data protection
        const string rootKey = "The root key protects every administrator session and belongs to the deployment; replacing it signs every administrator out.";
        Add("DataProtection:RootKey", "data-protection", "root-key", "Data Protection root key", ConfigurationValueKind.Secret,
            ConfigurationApply.ReadOnly, "Injected by the deployment; its value is never reported.", reason: rootKey);
        Add("DataProtection:RootKeyFile", "data-protection", "root-key", "Data Protection root key file",
            ConfigurationValueKind.Text, ConfigurationApply.ReadOnly, "Read when the deployment injects no root key.",
            "data/data-protection-root-key", reason: rootKey);

        // Database
        Add(ConnectionString, "database", "storage", "PostgreSQL connection string", ConfigurationValueKind.Text,
            ConfigurationApply.ReadOnly, "Required, with no default. The page reports the server and database alone, never the credential.",
            reason: "The running process is bound to this database; moving it is an offline operation with the host stopped.");
        Add("Database:InitializeOnStartup", "database", "storage", "Initialize database on startup", ConfigurationValueKind.Boolean,
            ConfigurationApply.Restart, "Create a missing database and apply pending migrations when the host starts.", "true");
        Add("Database:LegacySqlitePath", "database", "storage", "Legacy SQLite database path", ConfigurationValueKind.Text,
            ConfigurationApply.Restart, "Checked at startup for a database left by a release before PostgreSQL; a file there stops the start.",
            ApplicationFacts.LegacySqlitePath);
        Add("Database:SlowCommandThresholdMilliseconds", "database", "storage", "Slow command threshold (milliseconds)",
            ConfigurationValueKind.Integer, ConfigurationApply.Restart, "Database commands slower than this leave one warning; 0 turns the log off.",
            "500", 0, int.MaxValue);

        // Audio storage: the connection and credentials are live, the tuning limits are read at startup.
        Live("Audio:Enabled", "audio-storage", "connection", "Audio storage", ConfigurationValueKind.Boolean,
            "audioStorage", "enabled", null, "false");
        Live("Audio:InternalEndpoint", "audio-storage", "connection", "Internal endpoint", ConfigurationValueKind.Url,
            "audioStorage", "internalEndpoint", "The S3 address the server itself connects to.", "");
        Live("Audio:PublicEndpoint", "audio-storage", "connection", "Public endpoint", ConfigurationValueKind.Url,
            "audioStorage", "publicEndpoint", "The address browsers play audio from.", "");
        Live("Audio:Region", "audio-storage", "connection", "Region", ConfigurationValueKind.Text, "audioStorage", "region", null, "us-east-1");
        Live("Audio:Bucket", "audio-storage", "connection", "Bucket", ConfigurationValueKind.Text, "audioStorage", "bucket", null, "lexarbor-audio");
        Live("Audio:KeyPrefix", "audio-storage", "connection", "Key prefix", ConfigurationValueKind.Text, "audioStorage", "keyPrefix", null, "audio/");
        Live("Audio:AccessKey", "audio-storage", "credentials", "Access key", ConfigurationValueKind.Secret, "audioStorage", "accessKey");
        Live("Audio:SecretKey", "audio-storage", "credentials", "Secret key", ConfigurationValueKind.Secret, "audioStorage", "secretKey");
        const string advanced = "audio-storage";
        void Advanced(string name, string label, string description, string @default, long minimum, long maximum)
            => Add("Audio:" + name, advanced, "advanced", label, ConfigurationValueKind.Integer, ConfigurationApply.Restart,
                description, @default, minimum, maximum);
        Advanced("PlaybackTtlSeconds", "Playback link lifetime (seconds)", "How long a signed playback link stays valid.", "900", 60, 3600);
        Add("Audio:MaxUploadBytes", advanced, "advanced", "Maximum upload size (bytes)", ConfigurationValueKind.Number,
            ConfigurationApply.Restart, "Largest audio file one upload accepts.", "5242880", 1, 20 * 1024 * 1024);
        Advanced("MaxConcurrentUploads", "Concurrent upload limit", "Uploads admitted at the same time.", "2", 1, 8);
        Advanced("MaxQueuedUploads", "Queued upload limit", "Uploads waiting for admission; the rest are refused.", "8", 0, 8);
        Advanced("OperationTimeoutSeconds", "Operation timeout (seconds)", "Bound of one storage operation.", "60", 1, 120);
        Advanced("RecoveryBatchSize", "Recovery batch size", "Interrupted operations rechecked per pass.", "20", 1, 100);
        Advanced("RecoveryIntervalSeconds", "Recovery interval (seconds)", "Time between recovery passes.", "60", 10, 3600);
        Advanced("RecoveryLeaseSeconds", "Recovery lease (seconds)", "How long one operation may run before recovery reclaims it.", "300", 30, 900);
        Advanced("RecoveryRetrySeconds", "Recovery retry interval (seconds)", "First wait before a failed operation is retried.", "60", 10, 3600);
        Advanced("RecoveryMaxRetrySeconds", "Maximum recovery retry interval (seconds)", "Longest wait between recovery retries.", "3600", 10, 86400);
        Advanced("RecoveryRecheckSeconds", "Recovery recheck interval (seconds)", "How long a finished operation stays rechecked.", "86400", 3600, 604800);

        // Meaning submissions
        Add("MeaningSubmissions:BookId", "submissions", "feature", "Submission book ID", ConfigurationValueKind.Text,
            ConfigurationApply.Restart, "The book approved meanings are written into; empty turns submissions off.");

        // Vocabulary learning. These numbers share the whole number's ceiling rather than a
        // bound of their own: the binder reads them as 32-bit integers, and one beyond it is
        // refused here, because the binder would otherwise drop a list entry over it in silence.
        const string learning = "learning";
        Add("VocabularyLearning:SlowAnswerMs", learning, "general", "Slow answer threshold (milliseconds)", ConfigurationValueKind.Integer,
            ConfigurationApply.Restart, "An ordinary answer slower than this counts as slow.", "8000", 1, int.MaxValue);
        Add("VocabularyLearning:FillLettersSlowAnswerMs", learning, "general", "Fill-in-letters slow answer threshold (milliseconds)",
            ConfigurationValueKind.Integer, ConfigurationApply.Restart, "The same threshold for fill-in-letters questions.", "15000", 1, int.MaxValue);
        Add("VocabularyLearning:QuestionExpiryMinutes", learning, "general", "Question lifetime (minutes)", ConfigurationValueKind.Integer,
            ConfigurationApply.Restart, "How long an issued question may be answered.", "30", 1, int.MaxValue);
        Add("VocabularyLearning:TimeZone", learning, "general", "Time zone", ConfigurationValueKind.Text, ConfigurationApply.Restart,
            "Learning-day boundaries; empty keeps a fixed UTC+08:00 without daylight saving time.", nullable: true);
        const string fsrs = "fsrs";
        Add("VocabularyLearning:Fsrs:DesiredRetention", learning, fsrs, "Desired retention", ConfigurationValueKind.Number,
            ConfigurationApply.Restart, "Target probability of recall; greater than 0 and less than 1.", "0.9", 0, 1);
        Add("VocabularyLearning:Fsrs:FamiliarThresholdDays", learning, fsrs, "Familiar threshold (days)", ConfigurationValueKind.Number,
            ConfigurationApply.Restart, "Memory stability needed for the familiar stage; greater than 0.", "7", 0, int.MaxValue);
        Add("VocabularyLearning:Fsrs:MasteredThresholdDays", learning, fsrs, "Mastered threshold (days)", ConfigurationValueKind.Number,
            ConfigurationApply.Restart, "Memory stability needed for the mastered stage; greater than the familiar threshold.", "21", 0, int.MaxValue);
        // The options binder adds configured steps after the built-in ones (1, 10 and 10); no
        // configuration replaces those. The keys are therefore shown, and edited, as the extras.
        Add("VocabularyLearning:Fsrs:LearningStepsMinutes", learning, fsrs, "Additional learning steps (minutes)", ConfigurationValueKind.IntegerList,
            ConfigurationApply.Restart, "Steps that follow the built-in learning steps (1, 10) after a new word; each greater than 0.", null, 1, int.MaxValue);
        Add("VocabularyLearning:Fsrs:RelearningStepsMinutes", learning, fsrs, "Additional relearning steps (minutes)", ConfigurationValueKind.IntegerList,
            ConfigurationApply.Restart, "Steps that follow the built-in relearning step (10) after a forgotten word; each greater than 0.", null, 1, int.MaxValue);
        Add("VocabularyLearning:Fsrs:MaximumIntervalDays", learning, fsrs, "Maximum review interval (days)", ConfigurationValueKind.Integer,
            ConfigurationApply.Restart, "Longest gap between two reviews of one word.", "36500", 1, int.MaxValue);
        const string errorProne = "error-prone";
        Add("VocabularyLearning:ErrorProne:WindowDays", learning, errorProne, "History window (days)", ConfigurationValueKind.Integer,
            ConfigurationApply.Restart, "How much attempt history decides that a word is error-prone.", "30", 1, 3650);
        Add("VocabularyLearning:ErrorProne:MinimumAttempts", learning, errorProne, "Minimum attempts", ConfigurationValueKind.Integer,
            ConfigurationApply.Restart, "Attempts needed before the error-rate rule applies to a word.", "3", 1, int.MaxValue);
        Add("VocabularyLearning:ErrorProne:ErrorRateThreshold", learning, errorProne, "Error rate threshold", ConfigurationValueKind.Number,
            ConfigurationApply.Restart, "Share of wrong answers that marks a word as error-prone.", "0.4", 0, 1);
        Add("VocabularyLearning:ErrorProne:MinimumForgetCount", learning, errorProne, "Minimum forget count", ConfigurationValueKind.Integer,
            ConfigurationApply.Restart, "Times a word was forgotten that mark it as error-prone on their own.", "2", 1, int.MaxValue);

        // The learning service reaching this Lexarbor's own catalog
        const string link = "learning-connection";
        const string credential = "A key may only be sent to an HTTPS address, unless insecure HTTP is allowed.";
        Add("Lexarbor:BaseUrl", link, "catalog", "Catalog base URL", ConfigurationValueKind.Url, ConfigurationApply.Restart,
            "The base URL the learning service uses to reach this Lexarbor's catalog.", "");
        Add("Lexarbor:ApiKey", link, "catalog", "Catalog API key", ConfigurationValueKind.Secret, ConfigurationApply.Restart,
            "The key that reads the catalog; empty reads it without one.", "");
        Add("Lexarbor:AllowInsecureHttp", link, "catalog", "Allow insecure HTTP", ConfigurationValueKind.Boolean, ConfigurationApply.Restart,
            credential, "false");
        Add("Lexarbor:SupplementaryBookId", link, "catalog", "Supplementary book ID", ConfigurationValueKind.Text, ConfigurationApply.Restart,
            "The supplementary book, which ordinary book selection leaves out.", "");
        Add("Lexarbor:Integration:ApiKey", link, "integration", "Submission API key", ConfigurationValueKind.Secret, ConfigurationApply.Restart,
            "The separate key that sends meaning submissions; empty leaves that client disabled.", "");

        // Logging
        Add("Logging:LogLevel:Default", "logging", "levels", "Default log level", ConfigurationValueKind.Select,
            ConfigurationApply.Restart, "Minimum level of every category without its own rule.", "Information", options: Split(LogLevels));
        Add("Logging:LogLevel:Microsoft.AspNetCore", "logging", "levels", "ASP.NET Core log level", ConfigurationValueKind.Select,
            ConfigurationApply.Restart, null, "Warning", options: Split(LogLevels));
        Add("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "logging", "levels", "EF Core command log level",
            ConfigurationValueKind.Select, ConfigurationApply.Restart, null, "Warning", options: Split(LogLevels));

        // Telemetry
        const string authenticationValue = "The value lives only in the process environment and never enters configuration.";
        foreach (var signal in new[] { "Traces", "Metrics" })
        {
            var label = signal == "Traces" ? "traces" : "metrics";
            Add($"Telemetry:Otlp:{signal}:Enabled", "telemetry", signal.ToLowerInvariant(), $"Export {label}", ConfigurationValueKind.Boolean,
                ConfigurationApply.Restart, null, "false");
            Add($"Telemetry:Otlp:{signal}:Endpoint", "telemetry", signal.ToLowerInvariant(), $"{signal} endpoint", ConfigurationValueKind.Url,
                ConfigurationApply.Restart, "Empty keeps the exporter's default endpoint.", "");
            Add($"Telemetry:Otlp:{signal}:Protocol", "telemetry", signal.ToLowerInvariant(), $"{signal} protocol", ConfigurationValueKind.Select,
                ConfigurationApply.Restart, null, "Grpc", options: Split(OtlpProtocols));
            Add($"Telemetry:Otlp:{signal}:AuthenticationHeaderName", "telemetry", signal.ToLowerInvariant(), $"{signal} authentication header",
                ConfigurationValueKind.Text, ConfigurationApply.Restart,
                "Name of the header that carries the authorization value to the collector.", "");
        }
        fields.Add(new()
        {
            Key = ConfiguredOtlpAuthenticationResolver.ValueEnvironmentVariable,
            Group = "telemetry",
            Section = "authentication",
            Name = "OTLP authorization value",
            Kind = ConfigurationValueKind.Secret,
            Apply = ConfigurationApply.ReadOnly,
            Description = "Sent in every configured authentication header.",
            Reason = authenticationValue,
            EnvironmentVariable = true
        });

        // Service identity
        Add("Service:InstanceId", "service", "identity", "Service instance ID", ConfigurationValueKind.Text, ConfigurationApply.Restart,
            "Names this instance in structured logs; empty keeps a fresh random ID per start.");

        // Review assistant and models: one source of truth with the live module that validates them.
        // The module keeps its historical field names; the page shows the keys of this release.
        foreach (var field in EnrichmentConfigurationFields.All)
        {
            var bounded = field.Kind is EnrichmentFieldKind.Integer or EnrichmentFieldKind.Speed or EnrichmentFieldKind.Decimal;
            var (group, section) = Location(field.Name);
            // The connection keys of this release; the module keeps the names it has always had.
            var key = field.Key.StartsWith(ReasoningFields, StringComparison.Ordinal) ? Reasoning + field.Key[ModelConfigurationPaths.OldText.Length..]
                : field.Key.StartsWith(SpeechFields, StringComparison.Ordinal) ? Speech + field.Key[ModelConfigurationPaths.OldSpeech.Length..]
                : field.Key;
            var kind = field.Kind switch
            {
                EnrichmentFieldKind.Boolean => ConfigurationValueKind.Boolean,
                EnrichmentFieldKind.Integer => ConfigurationValueKind.Integer,
                EnrichmentFieldKind.Speed or EnrichmentFieldKind.Decimal => ConfigurationValueKind.Number,
                EnrichmentFieldKind.Emotion => ConfigurationValueKind.Select,
                EnrichmentFieldKind.String when field.Name.EndsWith("endpoint", StringComparison.Ordinal) => ConfigurationValueKind.Url,
                _ => field.Secret ? ConfigurationValueKind.Secret : ConfigurationValueKind.Text
            };
            fields.Add(new()
            {
                Key = key,
                Group = group,
                Section = section,
                // A field added without a friendly name still shows, under its module name.
                Name = Model.Names.GetValueOrDefault(field.Name, field.Name),
                Kind = kind,
                Apply = ConfigurationApply.Live,
                Default = DefaultText(field.Default),
                // Only a number has bounds; the module's other kinds carry a placeholder zero.
                Minimum = bounded ? field.Minimum : null,
                Maximum = bounded ? field.Maximum : null,
                Options = field.Kind == EnrichmentFieldKind.Emotion ? ["", "calm", "happy", "sad", "angry", "fearful", "disgusted", "surprised"] : null,
                Nullable = field.Kind == EnrichmentFieldKind.Speed,
                Module = "enrichment",
                Wire = field.Name,
                // A model connection is still read under its section before the upgrade; the module knows it there.
                ModuleKey = key == field.Key ? null : field.Key
            });
        }
        return fields;
    }

    private const string Reasoning = ModelConfigurationPaths.ReasoningKey;
    private const string Speech = ModelConfigurationPaths.SpeechKey;
    private const string ReasoningFields = ModelConfigurationPaths.OldText + ":";
    private const string SpeechFields = ModelConfigurationPaths.OldSpeech + ":";

    // Where a module field is shown. The reasoning and speech connections moved to the model
    // sections of this release; whichever section the deployment reads them from, they are shown there.
    // A module field is named by its dotted name, which keeps the sections' former titles.
    private static (string Group, string Section) Location(string name)
    {
        if (name.StartsWith("enrichment.text.", StringComparison.Ordinal)) return ("text", "reasoning");
        if (name.StartsWith("enrichment.tts.uk.", StringComparison.Ordinal)) return ("speech", "uk");
        if (name.StartsWith("enrichment.tts.us.", StringComparison.Ordinal)) return ("speech", "us");
        if (name.StartsWith("enrichment.tts.", StringComparison.Ordinal)) return ("speech", "tts");
        return name.StartsWith("reviewAssistant.", StringComparison.Ordinal) ? ("review", "review") : ("background", "policy");
    }

    /// <summary>A module default in configuration text form.</summary>
    private static string? DefaultText(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        System.Collections.IEnumerable list => string.Join(",", list.Cast<object>()
            .Select(item => item is IFormattable number ? number.ToString(null, CultureInfo.InvariantCulture) : item.ToString())),
        _ => null
    };

    // A nested type initializes on first use, so Build can read it whatever the field order.
    private static class Model
    {
        public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reviewAssistant.autoReviewEnabled"] = "Automatic submission review",
            ["enrichment.text.endpoint"] = "Reasoning service endpoint",
            ["enrichment.text.model"] = "Reasoning model",
            ["enrichment.text.apiKey"] = "Reasoning API key",
            ["enrichment.text.timeoutSeconds"] = "Reasoning timeout (seconds)",
            ["enrichment.tts.endpoint"] = "Speech service endpoint",
            ["enrichment.tts.model"] = "Speech model",
            ["enrichment.tts.apiKey"] = "Speech API key",
            ["enrichment.tts.timeoutSeconds"] = "Speech timeout (seconds)",
            ["enrichment.tts.uk.voice"] = "British voice",
            ["enrichment.tts.uk.style"] = "British style",
            ["enrichment.tts.uk.emotion"] = "British emotion",
            ["enrichment.tts.uk.pitch"] = "British pitch",
            ["enrichment.tts.uk.volume"] = "British volume",
            ["enrichment.tts.uk.speed"] = "British speed",
            ["enrichment.tts.us.voice"] = "American voice",
            ["enrichment.tts.us.style"] = "American style",
            ["enrichment.tts.us.emotion"] = "American emotion",
            ["enrichment.tts.us.pitch"] = "American pitch",
            ["enrichment.tts.us.volume"] = "American volume",
            ["enrichment.tts.us.speed"] = "American speed",
            ["enrichment.concurrency"] = "Target execution concurrency",
            ["enrichment.textConcurrency"] = "Text request concurrency",
            ["enrichment.failureBudget"] = "Per-step failure budget",
            ["enrichment.serviceFailureThreshold"] = "Service failure pause threshold",
            ["enrichment.servicePauseSeconds"] = "Service pause (seconds)",
            ["enrichment.maxServicePauseSeconds"] = "Maximum service pause (seconds)",
            ["enrichment.throttleInitialSeconds"] = "Throttle backoff (seconds)",
            ["enrichment.throttleMaxSeconds"] = "Maximum throttle backoff (seconds)",
            ["enrichment.discoveryBatchSize"] = "Discovery batch size",
            ["enrichment.scanIntervalSeconds"] = "Scan interval (seconds)"
        };
    }

    private static IReadOnlyList<ConfigurationCatalogGroup> BuildGroups() =>
    [
        new("identity", "access", "Sign-in and identity", "Who may sign in, and which identity provider is trusted."),
        new("rate-limits", "access", "Request rate limits", "A ceiling per client address on administrator sign-in."),
        new("network", "access", "Network and proxies", "Which upstream hops may state the client address."),
        new("data-protection", "access", "Data protection", "The root key behind every administrator session."),
        new("database", "data", "Database", "The PostgreSQL database, and how the host prepares it."),
        new("audio-storage", "data", "Audio storage", "The S3 bucket that holds pronunciation audio."),
        new("review", "ai", "Review assistant", "Automatic review of meaning submissions; its advice uses the reasoning model."),
        new("text", "ai", "Reasoning model", "The model behind photo import, review advice, completion and pronunciation planning."),
        new("speech", "ai", "Speech generation", "The speech service and one voice per accent."),
        new("background", "ai", "Background policies", "Discovery, concurrency, retries and pauses of background work."),
        new("learning", "learning", "Vocabulary learning", "Answer timing, review scheduling and error-prone words for learner practice."),
        new("learning-connection", "learning", "Learning service connection", "How the learning service reaches this Lexarbor's catalog and sends meaning submissions."),
        new("submissions", "operations", "Meaning submissions", "Where approved meanings are written."),
        new("logging", "operations", "Logging", "The minimum level of the console log."),
        new("telemetry", "operations", "Telemetry", "Optional OpenTelemetry traces and metrics."),
        new("service", "operations", "Service identity", "How this instance names itself in logs.")
    ];

    private static IReadOnlyList<ConfigurationCatalogSection> BuildSections() =>
    [
        new("identity", "provider", "Identity provider"),
        new("identity", "hosted-login", "Hosted login application"),
        new("identity", "access", "Access rules"),
        new("rate-limits", "admin-login", "Administrator login"),
        new("network", "forwarding", "Trusted forwarding"),
        new("data-protection", "root-key", "Root key"),
        new("database", "storage", "Connection and startup"),
        new("audio-storage", "connection", "Connection"),
        new("audio-storage", "credentials", "Credentials"),
        new("audio-storage", "advanced", "Upload, playback and recovery limits"),
        new("review", "review", "Review assistant"),
        new("text", "reasoning", "Reasoning model connection"),
        new("speech", "tts", "Speech service"),
        new("speech", "uk", "British English (UK)"),
        new("speech", "us", "American English (US)"),
        new("background", "policy", "Background policies"),
        new("learning", "general", "Answers and schedule"),
        new("learning", "fsrs", "Spaced repetition"),
        new("learning", "error-prone", "Error-prone words"),
        new("learning-connection", "catalog", "Catalog access"),
        new("learning-connection", "integration", "Meaning submissions"),
        new("submissions", "feature", "Submissions"),
        new("logging", "levels", "Log levels"),
        new("telemetry", "traces", "Traces"),
        new("telemetry", "metrics", "Metrics"),
        new("telemetry", "authentication", "Export authentication"),
        new("service", "identity", "Instance")
    ];
}
