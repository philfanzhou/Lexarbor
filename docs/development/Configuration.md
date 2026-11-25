# Lexarbor configuration keys

## Settings page

Open **系统设置** (`/#/settings`) to see and edit these keys. The overview counts what applies at once, what needs a restart and what the deployment owns; **全部配置** lists every key with search, filters and paging; each category has its own page. A key shows its value in effect, its source, and whether a change applies at once or at the next start. A key supplied by the environment or the command line is read-only there, with the reason. A restart-only key is validated the way startup validates before it is written, and the page marks it as waiting until the service restarts. See the [frontend guide](../frontend/README.md#system-settings) and the [catalog and startup settings contract](../api/SystemSettings.md#configuration-catalog-and-startup-settings).

## Models

Open **系统设置 → 视觉推理** or **系统设置 → 语音生成** to edit the server's model connections. An administrator can also place nested JSON in `/app/data/appsettings.json`, or set container environment variables using `__` for each `:`. For example, `Models:Reasoning:Model` becomes `Models__Reasoning__Model`. Pass the variable to the container itself. A variable exported only in the host shell may not reach it. The settings page applies successful saves immediately. Restart after editing the file or changing deployment overrides. Environment and command-line overrides are read-only in the settings page.

| Section | Keys within that section | How Lexarbor uses it |
|---|---|---|
| `Models:Reasoning` | `Endpoint`, `Model`, `ApiKey`, `TimeoutSeconds` | One OpenAI-compatible base URL and a model with image input and JSON output. Photo import sends the photo to this model directly. Review advice, entry completion, expansion and pronunciation planning share the connection. Photo import and review can use an endpoint that accepts requests without a key. Background enrichment requires a key. |
| `Models:Speech` | `Endpoint`, `Model`, `ApiKey`, `TimeoutSeconds` | Speech synthesis. The server identifies the speech protocol from the endpoint. The default model name is `speech-2.8-hd`. Choose a supported endpoint/model pair. |
| `Models:Speech:Uk` and `Models:Speech:Us` | `Voice`, `Speed`, `Style`, `Emotion`, `Pitch`, `Volume` | Set voice and speed for each accent you want to generate. `Style` is optional for supported OpenAI models. MiniMax supports optional emotion, pitch and volume. Both accents share the speech connection. |

`Endpoint` is the service base including its API version, such as `https://api.minimax.io/v1` for MiniMax speech. Both model timeouts default to 120 seconds and accept 1–300. The default applies when the setting is omitted. An explicit value in a file, environment variable or command-line argument still applies after an upgrade. Automatic pronunciation also needs the reasoning model for pronunciation planning and usable audio storage to publish the result. An administrator can preview a speech draft before those dependencies are ready. See the [speech support matrix](../api/Audio.md#speech-adapters) and [settings API](../api/SystemSettings.md).

### Upgrading

Back up the persistent settings file before upgrading. The server reads the previous `Enrichment:Text` or `MeaningSubmissions:Ai` reasoning connection and `Enrichment:Tts` speech connection. Configure the endpoint, model and credentials together under one section. Deployment overrides select their section first. With file settings, an explicit canonical section takes precedence, including when intentionally cleared. If both legacy reasoning connections were configured, the text connection takes precedence. Review the selected connection if the old models differed.

A successful settings-file write moves file-managed connections to `Models`, removes superseded connection fields and drops obsolete switches. Credentials supplied by the deployment stay in the deployment. Rename overrides **as a group** when upgrading, including values stored in the file for the same connection, then restart. A group with deployment overrides keeps its old file fields during the transition. Older binaries require the pre-upgrade file for rollback.

Removed operator settings include `VocabularyPhotoImport` (`Enabled`, `BaseUrl`, `ApiKey`, `RecognitionBudgetSeconds`, `MaxResponseBytes`), `Enrichment:FieldsEnabled`, `Enrichment:AudioEnabled` and `Enrichment:Tts:Provider`. The former textbook recording limits `Audio:MaxTextbookUploadBytes` and `Audio:MaxTextbookDurationSeconds` and the former per-signal `AllowInsecureLoopbackForTesting` OTLP keys are also pruned, along with the retired administrator password-login `Oidc` and `Gateway` sections and `CookieName` and `CookieSecure` settings. Remove obsolete deployment overrides from the deployment definition. Existing administrator HTTP clients keep the previous resource and field names. Private configuration-version metadata is managed by the server.

## Application defaults

Combine the section heading and the key with `:`. Values below are defaults before deployment overrides. Logging also supports named categories with inherited levels.

### `Models:Reasoning`

| Key within the section | Default | Purpose |
|---|---|---|
| `Endpoint` | `empty` | Base URL of the image-capable, OpenAI-compatible reasoning service. |
| `Model` | `empty` | Model ID for photo import, review, vocabulary completion and pronunciation planning. |
| `ApiKey` | `empty` | Bearer credential. Optional for photo import and review on an unauthenticated endpoint. Background enrichment requires it. |
| `TimeoutSeconds` | `120` | Time limit in seconds for one reasoning request, including photo recognition. |

### `Models:Speech`

| Key within the section | Default | Purpose |
|---|---|---|
| `Endpoint` | `empty` | Speech service base URL. The host selects the protocol from the supported endpoint. |
| `Model` | `speech-2.8-hd` | Speech synthesis model ID. |
| `ApiKey` | `empty` | Server-side speech service credential. |
| `TimeoutSeconds` | `120` | Time limit in seconds for one speech request, including a preview. |

### `Models:Speech:Uk` and `Models:Speech:Us`

| Key within the section | Default | Purpose |
|---|---|---|
| `Voice` | `empty` | Voice ID for this accent. |
| `Speed` | `null` | Playback speed multiplier used during synthesis. Each accent requires an explicit voice and speed. |
| `Style` | `empty` | Instructions for an OpenAI model that supports speech style. |
| `Emotion` | `empty` | MiniMax emotion. Empty leaves the choice to the provider. |
| `Pitch` | `0` | MiniMax pitch adjustment. Zero preserves the voice pitch. |
| `Volume` | `1` | MiniMax volume multiplier. |

### `Enrichment`

| Key within the section | Default | Purpose |
|---|---|---|
| `Concurrency` | `1` | Maximum concurrent target executions and speech calls. |
| `TextConcurrency` | `1` | Maximum concurrent background text preparation or expansion requests. |
| `FailureBudget` | `5` | Counted failures allowed for a task before automatic attempts stop. |
| `ServiceFailureThreshold` | `5` | Consecutive service failures before pausing the affected service. |
| `ServicePauseSeconds` | `60` | Initial pause in seconds before probing a failed service. |
| `MaxServicePauseSeconds` | `600` | Maximum service pause in seconds between recovery probes. |
| `ThrottleInitialSeconds` | `8` | Initial backoff in seconds after rate limiting. |
| `ThrottleMaxSeconds` | `120` | Maximum computed rate-limit backoff in seconds. A longer provider Retry-After takes precedence. |
| `DiscoveryBatchSize` | `20` | Maximum work discovered per batch. |
| `ScanIntervalSeconds` | `30` | Seconds between background scans for outstanding work. |

### `MeaningSubmissions`

| Key within the section | Default | Purpose |
|---|---|---|
| `BookId` | `unset` | Destination book for approved submissions. Submissions and approval require a configured, enabled book. |

### `MeaningSubmissions:Ai`

| Key within the section | Default | Purpose |
|---|---|---|
| `AutoReviewEnabled` | `false` | Authorize automatic submission decisions that can update the catalog. |

### `ConnectionStrings`

| Key within the section | Default | Purpose |
|---|---|---|
| `Default` | `required` | PostgreSQL connection string for the business database. |

### `Database`

| Key within the section | Default | Purpose |
|---|---|---|
| `InitializeOnStartup` | `true` | Run database initialization and migrations at startup. |
| `LegacySqlitePath` | `data/vocabulary.db` | Path checked for a legacy SQLite database when verifying an upgrade. |
| `SlowCommandThresholdMilliseconds` | `500` | Log database commands exceeding this duration in milliseconds. Zero disables the slow-command log. |

### `Service`

| Key within the section | Default | Purpose |
|---|---|---|
| `InstanceId` | `random per start` | Instance identifier for diagnostic correlation. |

### `Logging:LogLevel`

| Key within the section | Default | Purpose |
|---|---|---|
| `Default` | `Information` | Default minimum log level. |
| `Microsoft.AspNetCore` | `Warning` | Minimum level for the ASP.NET Core category. |
| `Microsoft.EntityFrameworkCore.Database.Command` | `Warning` | Minimum level for EF Core command logging. |

### `DataProtection`

| Key within the section | Default | Purpose |
|---|---|---|
| `RootKey` | `unset` | Injected root key protecting persisted administrator session keys. When unset, the host uses the key file. |
| `RootKeyFile` | `data/data-protection-root-key` | Key file path when no root key is injected. A missing file is generated on first use. |

### `IdentityService`

| Key within the section | Default | Purpose |
|---|---|---|
| `Authority` | `http://localhost:8080` | Base address for identity-provider discovery. |
| `Issuer` | `http://localhost:8080` | Expected token issuer. |
| `Audience` | `lexarbor` | Expected token audience. |
| `RequireHttpsMetadata` | `derived` | Require HTTPS for identity metadata. Unset resolves to false in Development, Testing or for a loopback authority, otherwise true. |

### `AdminAuthentication`

| Key within the section | Default | Purpose |
|---|---|---|
| `RequiredRole` | `admin` | Role required for administrator access. |

### `AdminAuthentication:OidcCode`

| Key within the section | Default | Purpose |
|---|---|---|
| `ClientId` | `empty` | Hosted administrator login client ID. |
| `ClientSecret` | `empty` | Hosted administrator login client secret. |
| `RedirectUri` | `empty` | Registered callback URL for administrator sign-in. |
| `PostLogoutRedirectUri` | `empty` | Optional registered return URL for prepared logout. |
| `Scope` | `openid profile` | Space-delimited scopes requested at administrator sign-in. |

### `RateLimits:AdminLogin`

| Key within the section | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Enable per-client-address throttling on administrator sign-in initiation. |
| `PermitLimit` | `10` | Requests allowed per client address per window when enabled. |
| `WindowSeconds` | `300` | Sliding-window duration in seconds. |

### `Network`

| Key within the section | Default | Purpose |
|---|---|---|
| `TrustedProxies` | `[]` | Individual proxy addresses trusted to forward client addresses. |
| `TrustedNetworks` | `[]` | Trusted proxy networks in CIDR notation. |
| `ForwardLimit` | `1` | Maximum number of trusted forwarding hops. |

### `Audio`

| Key within the section | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Enable object storage for audio uploads, playback links and recovery. |
| `InternalEndpoint` | `empty` | Object storage address used by the server. |
| `PublicEndpoint` | `empty` | Public address used to sign playback links. |
| `AccessKey` | `empty` | Server-side object storage access key ID. |
| `SecretKey` | `empty` | Server-side object storage access secret. |
| `Region` | `us-east-1` | Object storage signing region. |
| `Bucket` | `lexarbor-audio` | Bucket containing audio objects. |
| `KeyPrefix` | `audio/` | Prefix for stored audio objects. |
| `PlaybackTtlSeconds` | `900` | Lifetime of a signed playback link in seconds. |
| `MaxUploadBytes` | `5242880` | Maximum uploaded audio file size in bytes. |
| `MaxConcurrentUploads` | `2` | Maximum simultaneous admitted uploads. |
| `MaxQueuedUploads` | `8` | Maximum uploads that may wait for admission. |
| `OperationTimeoutSeconds` | `60` | Time budget per audio storage operation in seconds. |
| `RecoveryBatchSize` | `20` | Maximum recovery records processed per batch for each type of work. |
| `RecoveryIntervalSeconds` | `60` | Seconds between recovery batches. |
| `RecoveryLeaseSeconds` | `300` | Seconds a worker may hold a recovery record. |
| `RecoveryRetrySeconds` | `60` | Initial delay in seconds before retrying a failed recovery. |
| `RecoveryMaxRetrySeconds` | `3600` | Maximum recovery retry delay in seconds. |
| `RecoveryRecheckSeconds` | `86400` | Seconds before rechecking an object previously observed deleted. |

### `Lexarbor`

| Key within the section | Default | Purpose |
|---|---|---|
| `BaseUrl` | `empty` | Base URL used by the learning service to reach the catalog. |
| `ApiKey` | `empty` | Credential for reading the catalog. |
| `AllowInsecureHttp` | `false` | Allow credentials to be sent to a configured HTTP service. |
| `SupplementaryBookId` | `empty` | Identify the supplementary book to exclude from ordinary book selection. |

### `Lexarbor:Integration`

| Key within the section | Default | Purpose |
|---|---|---|
| `ApiKey` | `empty` | Separate credential for sending meaning submissions. |

### `VocabularyLearning`

| Key within the section | Default | Purpose |
|---|---|---|
| `SlowAnswerMs` | `8000` | Slow-answer threshold in milliseconds for ordinary questions. |
| `FillLettersSlowAnswerMs` | `15000` | Slow-answer threshold in milliseconds for fill-in-letters questions. |
| `QuestionExpiryMinutes` | `30` | Lifetime of an issued question in minutes. |
| `TimeZone` | `fixed UTC+08:00` | Time zone for learning-day boundaries. The fallback is a fixed offset without daylight saving time. |

### `VocabularyLearning:Fsrs`

| Key within the section | Default | Purpose |
|---|---|---|
| `DesiredRetention` | `0.9` | Target probability of recall used by spaced repetition. |
| `FamiliarThresholdDays` | `7` | Memory stability in days required for the familiar stage. |
| `MasteredThresholdDays` | `21` | Memory stability in days required for the mastered stage. |
| `LearningStepsMinutes` | `[1, 10]` | Initial learning steps in minutes. |
| `RelearningStepsMinutes` | `[10]` | Relearning steps after forgetting, in minutes. |
| `MaximumIntervalDays` | `36500` | Maximum review interval in days. |

Configured steps follow the built-in ones: the options binder adds the entries of `LearningStepsMinutes` and `RelearningStepsMinutes` after `1, 10` and `10`, which no configuration replaces. The settings page therefore shows and edits these two keys as the additional steps.

### `VocabularyLearning:ErrorProne`

| Key within the section | Default | Purpose |
|---|---|---|
| `WindowDays` | `30` | Number of days of history evaluated for error-prone words. |
| `MinimumAttempts` | `3` | Minimum attempts before the error-rate criterion applies. |
| `ErrorRateThreshold` | `0.4` | Minimum error proportion for the error-rate criterion. |
| `MinimumForgetCount` | `2` | Recorded forgetting count that independently qualifies a word. |

### `Telemetry:Otlp:Traces` and `Telemetry:Otlp:Metrics`

| Key within the section | Default | Purpose |
|---|---|---|
| `Enabled` | `false` | Export this signal to an OTLP collector. |
| `Endpoint` | `unset` | Collector endpoint for this signal. |
| `Protocol` | `Grpc` | Transport protocol for this signal. HttpProtobuf is also supported. |
| `AuthenticationHeaderName` | `unset` | Optional name of the collector credential header. Its value comes from LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION. |

The OTLP header value is the environment-only variable `LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION` (unset by default).

## Container launch settings

| Variable | Default | Entry point and purpose |
|---|---|---|
| `LEXARBOR_IMAGE` | `lexarbor:latest` | Application image for either launcher. |
| `LEXARBOR_PORT` | `5008` | Published host port for either launcher. |
| `TZ` | `UTC` | Application container time zone for either launcher. Learning days have their own setting. |
| `LEXARBOR_CONTAINER_NAME` | `lexarbor` | Standalone container name. |
| `LEXARBOR_NETWORK` | `lexarbor-net` | Standalone Docker network. |
| `LEXARBOR_DATA_DIR` | `<repository>/data` | Standalone host directory bound to `/app/data`. |
| `LEXARBOR_POSTGRES_IMAGE` | `postgres:16` | PostgreSQL image used by Compose. |
| `LEXARBOR_DB_PASSWORD` | required | Password used for the Compose database and the application connection. |

The [launcher](../../scripts/start.sh) also maps the `LEXARBOR_*` aliases for application settings such as `LEXARBOR_DATABASE_CONNECTION_STRING` to their corresponding container keys. To set model variables with Compose, add `Models__Reasoning__...` and `Models__Speech__...` in the Lexarbor service's `environment` block. With the standalone launcher, use the admin settings page or the mounted JSON file.

### Standalone launcher aliases

An unset alias supplies no override. With an authority and no issuer override, the launcher passes the authority as the issuer. See the corresponding application key above for its default and purpose.

| Shell variable | Container configuration key |
|---|---|
| `LEXARBOR_ADMIN_AUTH_PROVIDER` | `AdminAuthentication:Provider (retired login provider check)` |
| `LEXARBOR_AUDIO_ACCESS_KEY` | `Audio:AccessKey` |
| `LEXARBOR_AUDIO_BUCKET` | `Audio:Bucket` |
| `LEXARBOR_AUDIO_ENABLED` | `Audio:Enabled` |
| `LEXARBOR_AUDIO_INTERNAL_ENDPOINT` | `Audio:InternalEndpoint` |
| `LEXARBOR_AUDIO_KEY_PREFIX` | `Audio:KeyPrefix` |
| `LEXARBOR_AUDIO_MAX_CONCURRENT_UPLOADS` | `Audio:MaxConcurrentUploads` |
| `LEXARBOR_AUDIO_MAX_QUEUED_UPLOADS` | `Audio:MaxQueuedUploads` |
| `LEXARBOR_AUDIO_MAX_UPLOAD_BYTES` | `Audio:MaxUploadBytes` |
| `LEXARBOR_AUDIO_OPERATION_TIMEOUT_SECONDS` | `Audio:OperationTimeoutSeconds` |
| `LEXARBOR_AUDIO_PLAYBACK_TTL_SECONDS` | `Audio:PlaybackTtlSeconds` |
| `LEXARBOR_AUDIO_PUBLIC_ENDPOINT` | `Audio:PublicEndpoint` |
| `LEXARBOR_AUDIO_RECOVERY_BATCH_SIZE` | `Audio:RecoveryBatchSize` |
| `LEXARBOR_AUDIO_RECOVERY_INTERVAL_SECONDS` | `Audio:RecoveryIntervalSeconds` |
| `LEXARBOR_AUDIO_RECOVERY_LEASE_SECONDS` | `Audio:RecoveryLeaseSeconds` |
| `LEXARBOR_AUDIO_RECOVERY_MAX_RETRY_SECONDS` | `Audio:RecoveryMaxRetrySeconds` |
| `LEXARBOR_AUDIO_RECOVERY_RECHECK_SECONDS` | `Audio:RecoveryRecheckSeconds` |
| `LEXARBOR_AUDIO_RECOVERY_RETRY_SECONDS` | `Audio:RecoveryRetrySeconds` |
| `LEXARBOR_AUDIO_REGION` | `Audio:Region` |
| `LEXARBOR_AUDIO_SECRET_KEY` | `Audio:SecretKey` |
| `LEXARBOR_DATABASE_CONNECTION_STRING` | `ConnectionStrings:Default` |
| `LEXARBOR_DATA_PROTECTION_ROOT_KEY` | `DataProtection:RootKey` |
| `LEXARBOR_IDENTITY_AUDIENCE` | `IdentityService:Audience` |
| `LEXARBOR_IDENTITY_AUTHORITY` | `IdentityService:Authority` |
| `LEXARBOR_IDENTITY_ISSUER` | `IdentityService:Issuer` |
| `LEXARBOR_INSTANCE_ID` | `Service:InstanceId` |
| `LEXARBOR_LEGACY_SQLITE_PATH` | `Database:LegacySqlitePath` |
| `LEXARBOR_OIDC_CODE_CLIENT_ID` | `AdminAuthentication:OidcCode:ClientId` |
| `LEXARBOR_OIDC_CODE_CLIENT_SECRET` | `AdminAuthentication:OidcCode:ClientSecret` |
| `LEXARBOR_OIDC_CODE_POST_LOGOUT_REDIRECT_URI` | `AdminAuthentication:OidcCode:PostLogoutRedirectUri` |
| `LEXARBOR_OIDC_CODE_REDIRECT_URI` | `AdminAuthentication:OidcCode:RedirectUri` |
| `LEXARBOR_OIDC_CODE_SCOPE` | `AdminAuthentication:OidcCode:Scope` |
| `LEXARBOR_REQUIRE_HTTPS_METADATA` | `IdentityService:RequireHttpsMetadata` |
| `LEXARBOR_TELEMETRY_OTLP_METRICS_AUTHENTICATION_HEADER_NAME` | `Telemetry:Otlp:Metrics:AuthenticationHeaderName` |
| `LEXARBOR_TELEMETRY_OTLP_METRICS_ENABLED` | `Telemetry:Otlp:Metrics:Enabled` |
| `LEXARBOR_TELEMETRY_OTLP_METRICS_ENDPOINT` | `Telemetry:Otlp:Metrics:Endpoint` |
| `LEXARBOR_TELEMETRY_OTLP_METRICS_PROTOCOL` | `Telemetry:Otlp:Metrics:Protocol` |
| `LEXARBOR_TELEMETRY_OTLP_TRACES_AUTHENTICATION_HEADER_NAME` | `Telemetry:Otlp:Traces:AuthenticationHeaderName` |
| `LEXARBOR_TELEMETRY_OTLP_TRACES_ENABLED` | `Telemetry:Otlp:Traces:Enabled` |
| `LEXARBOR_TELEMETRY_OTLP_TRACES_ENDPOINT` | `Telemetry:Otlp:Traces:Endpoint` |
| `LEXARBOR_TELEMETRY_OTLP_TRACES_PROTOCOL` | `Telemetry:Otlp:Traces:Protocol` |

## Image build arguments

| Argument | Default | Purpose |
|---|---|---|
| `NPM_REGISTRY` | `https://registry.npmjs.org/` | Registry for frontend dependencies. |
| `BUILD_CONFIGURATION` | `Release` | .NET publish configuration. |
| `APP_VERSION` | `0.0.0-dev` | Application version embedded in the build. |
| `APP_REVISION` | empty | Source revision embedded in the build. |
| `APP_CHANNEL` | `development` | Release channel embedded in the build. |
