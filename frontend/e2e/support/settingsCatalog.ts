import type { Page, Route } from '@playwright/test'
import { mockAntiforgery } from './antiforgery'

/**
 * A settings catalog with the server's shape, generated from the real endpoint's answer.
 * Each definition is one key:
 * [key, group, section, kind, apply, default, minimum, maximum, options, module, wire, nullable].
 */
const definitions: Definition[] = [
  ["IdentityService:Authority", "identity", "provider", "url", "restart", "http://localhost:8080", null, null, null, null, null, false],
  ["IdentityService:Issuer", "identity", "provider", "text", "restart", "http://localhost:8080", null, null, null, null, null, false],
  ["IdentityService:Audience", "identity", "provider", "text", "restart", "lexarbor", null, null, null, null, null, false],
  ["IdentityService:RequireHttpsMetadata", "identity", "provider", "triState", "restart", null, null, null, null, null, null, false],
  ["AdminAuthentication:OidcCode:ClientId", "identity", "hosted-login", "text", "restart", "", null, null, null, null, null, false],
  ["AdminAuthentication:OidcCode:ClientSecret", "identity", "hosted-login", "secret", "restart", null, null, null, null, null, null, false],
  ["AdminAuthentication:OidcCode:RedirectUri", "identity", "hosted-login", "url", "restart", "", null, null, null, null, null, false],
  ["AdminAuthentication:OidcCode:PostLogoutRedirectUri", "identity", "hosted-login", "url", "restart", "", null, null, null, null, null, false],
  ["AdminAuthentication:OidcCode:Scope", "identity", "hosted-login", "text", "restart", "openid profile", null, null, null, null, null, false],
  ["AdminAuthentication:RequiredRole", "identity", "access", "text", "restart", "admin", null, null, null, null, null, false],
  ["AdminAuthentication:Provider", "identity", "access", "text", "readOnly", "", null, null, null, null, null, false],
  ["AdminAuthentication:HttpTestOrigins", "identity", "access", "text", "readOnly", "", null, null, null, null, null, false],
  ["RateLimits:AdminLogin:Enabled", "rate-limits", "admin-login", "boolean", "live", false, null, null, null, "rateLimits", "adminLoginEnabled", false],
  ["RateLimits:AdminLogin:PermitLimit", "rate-limits", "admin-login", "integer", "restart", 10, 1, 10000, null, null, null, false],
  ["RateLimits:AdminLogin:WindowSeconds", "rate-limits", "admin-login", "integer", "restart", 300, 10, 600, null, null, null, false],
  ["Network:TrustedProxies", "network", "forwarding", "list", "restart", [], null, 64, null, null, null, false],
  ["Network:TrustedNetworks", "network", "forwarding", "list", "restart", [], null, 64, null, null, null, false],
  ["Network:ForwardLimit", "network", "forwarding", "integer", "restart", 1, 1, 10, null, null, null, false],
  ["DataProtection:RootKey", "data-protection", "root-key", "secret", "readOnly", null, null, null, null, null, null, false],
  ["DataProtection:RootKeyFile", "data-protection", "root-key", "text", "readOnly", "data/data-protection-root-key", null, null, null, null, null, false],
  ["ConnectionStrings:Default", "database", "storage", "text", "readOnly", null, null, null, null, null, null, false],
  ["Database:InitializeOnStartup", "database", "storage", "boolean", "restart", true, null, null, null, null, null, false],
  ["Database:LegacySqlitePath", "database", "storage", "text", "restart", "data/vocabulary.db", null, null, null, null, null, false],
  ["Database:SlowCommandThresholdMilliseconds", "database", "storage", "integer", "restart", 500, 0, 2147483647, null, null, null, false],
  ["Audio:Enabled", "audio-storage", "connection", "boolean", "live", false, null, null, null, "audioStorage", "enabled", false],
  ["Audio:InternalEndpoint", "audio-storage", "connection", "url", "live", "", null, null, null, "audioStorage", "internalEndpoint", false],
  ["Audio:PublicEndpoint", "audio-storage", "connection", "url", "live", "", null, null, null, "audioStorage", "publicEndpoint", false],
  ["Audio:Region", "audio-storage", "connection", "text", "live", "us-east-1", null, null, null, "audioStorage", "region", false],
  ["Audio:Bucket", "audio-storage", "connection", "text", "live", "lexarbor-audio", null, null, null, "audioStorage", "bucket", false],
  ["Audio:KeyPrefix", "audio-storage", "connection", "text", "live", "audio/", null, null, null, "audioStorage", "keyPrefix", false],
  ["Audio:AccessKey", "audio-storage", "credentials", "secret", "live", null, null, null, null, "audioStorage", "accessKey", false],
  ["Audio:SecretKey", "audio-storage", "credentials", "secret", "live", null, null, null, null, "audioStorage", "secretKey", false],
  ["Audio:PlaybackTtlSeconds", "audio-storage", "advanced", "integer", "restart", 900, 60, 3600, null, null, null, false],
  ["Audio:MaxUploadBytes", "audio-storage", "advanced", "number", "restart", 5242880, 1, 20971520, null, null, null, false],
  ["Audio:MaxConcurrentUploads", "audio-storage", "advanced", "integer", "restart", 2, 1, 8, null, null, null, false],
  ["Audio:MaxQueuedUploads", "audio-storage", "advanced", "integer", "restart", 8, 0, 8, null, null, null, false],
  ["Audio:OperationTimeoutSeconds", "audio-storage", "advanced", "integer", "restart", 60, 1, 120, null, null, null, false],
  ["Audio:RecoveryBatchSize", "audio-storage", "advanced", "integer", "restart", 20, 1, 100, null, null, null, false],
  ["Audio:RecoveryIntervalSeconds", "audio-storage", "advanced", "integer", "restart", 60, 10, 3600, null, null, null, false],
  ["Audio:RecoveryLeaseSeconds", "audio-storage", "advanced", "integer", "restart", 300, 30, 900, null, null, null, false],
  ["Audio:RecoveryRetrySeconds", "audio-storage", "advanced", "integer", "restart", 60, 10, 3600, null, null, null, false],
  ["Audio:RecoveryMaxRetrySeconds", "audio-storage", "advanced", "integer", "restart", 3600, 10, 86400, null, null, null, false],
  ["Audio:RecoveryRecheckSeconds", "audio-storage", "advanced", "integer", "restart", 86400, 3600, 604800, null, null, null, false],
  ["MeaningSubmissions:BookId", "submissions", "feature", "text", "restart", null, null, null, null, null, null, false],
  ["VocabularyLearning:SlowAnswerMs", "learning", "general", "integer", "restart", 8000, 1, 2147483647, null, null, null, false],
  ["VocabularyLearning:FillLettersSlowAnswerMs", "learning", "general", "integer", "restart", 15000, 1, 2147483647, null, null, null, false],
  ["VocabularyLearning:QuestionExpiryMinutes", "learning", "general", "integer", "restart", 30, 1, 2147483647, null, null, null, false],
  ["VocabularyLearning:TimeZone", "learning", "general", "text", "restart", null, null, null, null, null, null, true],
  ["VocabularyLearning:Fsrs:DesiredRetention", "learning", "fsrs", "number", "restart", 0.9, 0, 1, null, null, null, false],
  ["VocabularyLearning:Fsrs:FamiliarThresholdDays", "learning", "fsrs", "number", "restart", 7, 0, 2147483647, null, null, null, false],
  ["VocabularyLearning:Fsrs:MasteredThresholdDays", "learning", "fsrs", "number", "restart", 21, 0, 2147483647, null, null, null, false],
  ["VocabularyLearning:Fsrs:LearningStepsMinutes", "learning", "fsrs", "integerList", "restart", [], 1, 2147483647, null, null, null, false],
  ["VocabularyLearning:Fsrs:RelearningStepsMinutes", "learning", "fsrs", "integerList", "restart", [], 1, 2147483647, null, null, null, false],
  ["VocabularyLearning:Fsrs:MaximumIntervalDays", "learning", "fsrs", "integer", "restart", 36500, 1, 2147483647, null, null, null, false],
  ["VocabularyLearning:ErrorProne:WindowDays", "learning", "error-prone", "integer", "restart", 30, 1, 3650, null, null, null, false],
  ["VocabularyLearning:ErrorProne:MinimumAttempts", "learning", "error-prone", "integer", "restart", 3, 1, 2147483647, null, null, null, false],
  ["VocabularyLearning:ErrorProne:ErrorRateThreshold", "learning", "error-prone", "number", "restart", 0.4, 0, 1, null, null, null, false],
  ["VocabularyLearning:ErrorProne:MinimumForgetCount", "learning", "error-prone", "integer", "restart", 2, 1, 2147483647, null, null, null, false],
  ["Lexarbor:BaseUrl", "learning-connection", "catalog", "url", "restart", "", null, null, null, null, null, false],
  ["Lexarbor:ApiKey", "learning-connection", "catalog", "secret", "restart", null, null, null, null, null, null, false],
  ["Lexarbor:AllowInsecureHttp", "learning-connection", "catalog", "boolean", "restart", false, null, null, null, null, null, false],
  ["Lexarbor:SupplementaryBookId", "learning-connection", "catalog", "text", "restart", "", null, null, null, null, null, false],
  ["Lexarbor:Integration:ApiKey", "learning-connection", "integration", "secret", "restart", null, null, null, null, null, null, false],
  ["Logging:LogLevel:Default", "logging", "levels", "select", "restart", "Information", null, null, ["Trace","Debug","Information","Warning","Error","Critical","None"], null, null, false],
  ["Logging:LogLevel:Microsoft.AspNetCore", "logging", "levels", "select", "restart", "Warning", null, null, ["Trace","Debug","Information","Warning","Error","Critical","None"], null, null, false],
  ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "logging", "levels", "select", "restart", "Warning", null, null, ["Trace","Debug","Information","Warning","Error","Critical","None"], null, null, false],
  ["Telemetry:Otlp:Traces:Enabled", "telemetry", "traces", "boolean", "restart", false, null, null, null, null, null, false],
  ["Telemetry:Otlp:Traces:Endpoint", "telemetry", "traces", "url", "restart", "", null, null, null, null, null, false],
  ["Telemetry:Otlp:Traces:Protocol", "telemetry", "traces", "select", "restart", "Grpc", null, null, ["Grpc","HttpProtobuf"], null, null, false],
  ["Telemetry:Otlp:Traces:AuthenticationHeaderName", "telemetry", "traces", "text", "restart", "", null, null, null, null, null, false],
  ["Telemetry:Otlp:Metrics:Enabled", "telemetry", "metrics", "boolean", "restart", false, null, null, null, null, null, false],
  ["Telemetry:Otlp:Metrics:Endpoint", "telemetry", "metrics", "url", "restart", "", null, null, null, null, null, false],
  ["Telemetry:Otlp:Metrics:Protocol", "telemetry", "metrics", "select", "restart", "Grpc", null, null, ["Grpc","HttpProtobuf"], null, null, false],
  ["Telemetry:Otlp:Metrics:AuthenticationHeaderName", "telemetry", "metrics", "text", "restart", "", null, null, null, null, null, false],
  ["LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION", "telemetry", "authentication", "secret", "readOnly", null, null, null, null, null, null, false],
  ["Service:InstanceId", "service", "identity", "text", "restart", null, null, null, null, null, null, false],
  ["MeaningSubmissions:Ai:AutoReviewEnabled", "review", "review", "boolean", "live", false, null, null, null, "enrichment", "reviewAssistant.autoReviewEnabled", false],
  ["Models:Reasoning:Endpoint", "text", "reasoning", "url", "live", "", null, null, null, "enrichment", "enrichment.text.endpoint", false],
  ["Models:Reasoning:Model", "text", "reasoning", "text", "live", "", null, null, null, "enrichment", "enrichment.text.model", false],
  ["Models:Reasoning:ApiKey", "text", "reasoning", "secret", "live", null, null, null, null, "enrichment", "enrichment.text.apiKey", false],
  ["Models:Reasoning:TimeoutSeconds", "text", "reasoning", "integer", "live", 120, 1, 300, null, "enrichment", "enrichment.text.timeoutSeconds", false],
  ["Models:Speech:Endpoint", "speech", "tts", "url", "live", "", null, null, null, "enrichment", "enrichment.tts.endpoint", false],
  ["Models:Speech:Model", "speech", "tts", "text", "live", "speech-2.8-hd", null, null, null, "enrichment", "enrichment.tts.model", false],
  ["Models:Speech:ApiKey", "speech", "tts", "secret", "live", null, null, null, null, "enrichment", "enrichment.tts.apiKey", false],
  ["Models:Speech:TimeoutSeconds", "speech", "tts", "integer", "live", 120, 1, 300, null, "enrichment", "enrichment.tts.timeoutSeconds", false],
  ["Models:Speech:Uk:Voice", "speech", "uk", "text", "live", "", null, null, null, "enrichment", "enrichment.tts.uk.voice", false],
  ["Models:Speech:Uk:Style", "speech", "uk", "text", "live", "", null, null, null, "enrichment", "enrichment.tts.uk.style", false],
  ["Models:Speech:Uk:Emotion", "speech", "uk", "select", "live", "", null, null, ["","calm","happy","sad","angry","fearful","disgusted","surprised"], "enrichment", "enrichment.tts.uk.emotion", false],
  ["Models:Speech:Uk:Pitch", "speech", "uk", "integer", "live", 0, -12, 12, null, "enrichment", "enrichment.tts.uk.pitch", false],
  ["Models:Speech:Uk:Volume", "speech", "uk", "number", "live", 1, 0.1, 10, null, "enrichment", "enrichment.tts.uk.volume", false],
  ["Models:Speech:Uk:Speed", "speech", "uk", "number", "live", null, 0.1, 4, null, "enrichment", "enrichment.tts.uk.speed", true],
  ["Models:Speech:Us:Voice", "speech", "us", "text", "live", "", null, null, null, "enrichment", "enrichment.tts.us.voice", false],
  ["Models:Speech:Us:Style", "speech", "us", "text", "live", "", null, null, null, "enrichment", "enrichment.tts.us.style", false],
  ["Models:Speech:Us:Emotion", "speech", "us", "select", "live", "", null, null, ["","calm","happy","sad","angry","fearful","disgusted","surprised"], "enrichment", "enrichment.tts.us.emotion", false],
  ["Models:Speech:Us:Pitch", "speech", "us", "integer", "live", 0, -12, 12, null, "enrichment", "enrichment.tts.us.pitch", false],
  ["Models:Speech:Us:Volume", "speech", "us", "number", "live", 1, 0.1, 10, null, "enrichment", "enrichment.tts.us.volume", false],
  ["Models:Speech:Us:Speed", "speech", "us", "number", "live", null, 0.1, 4, null, "enrichment", "enrichment.tts.us.speed", true],
  ["Enrichment:Concurrency", "background", "policy", "integer", "live", 1, 1, 8, null, "enrichment", "enrichment.concurrency", false],
  ["Enrichment:TextConcurrency", "background", "policy", "integer", "live", 1, 1, 2, null, "enrichment", "enrichment.textConcurrency", false],
  ["Enrichment:FailureBudget", "background", "policy", "integer", "live", 5, 1, 20, null, "enrichment", "enrichment.failureBudget", false],
  ["Enrichment:ServiceFailureThreshold", "background", "policy", "integer", "live", 5, 1, 20, null, "enrichment", "enrichment.serviceFailureThreshold", false],
  ["Enrichment:ServicePauseSeconds", "background", "policy", "integer", "live", 60, 1, 600, null, "enrichment", "enrichment.servicePauseSeconds", false],
  ["Enrichment:MaxServicePauseSeconds", "background", "policy", "integer", "live", 600, 1, 3600, null, "enrichment", "enrichment.maxServicePauseSeconds", false],
  ["Enrichment:ThrottleInitialSeconds", "background", "policy", "integer", "live", 8, 1, 120, null, "enrichment", "enrichment.throttleInitialSeconds", false],
  ["Enrichment:ThrottleMaxSeconds", "background", "policy", "integer", "live", 120, 1, 3600, null, "enrichment", "enrichment.throttleMaxSeconds", false],
  ["Enrichment:DiscoveryBatchSize", "background", "policy", "integer", "live", 20, 1, 1000, null, "enrichment", "enrichment.discoveryBatchSize", false],
  ["Enrichment:ScanIntervalSeconds", "background", "policy", "integer", "live", 30, 1, 3600, null, "enrichment", "enrichment.scanIntervalSeconds", false],
]

const groupDefinitions = [
  ["identity", "access", "Sign-in and identity", "Who may sign in, and which identity provider is trusted."],
  ["rate-limits", "access", "Request rate limits", "A ceiling per client address on administrator sign-in."],
  ["network", "access", "Network and proxies", "Which upstream hops may state the client address."],
  ["data-protection", "access", "Data protection", "The root key behind every administrator session."],
  ["database", "data", "Database", "The PostgreSQL database, and how the host prepares it."],
  ["audio-storage", "data", "Audio storage", "The S3 bucket that holds pronunciation audio."],
  ["review", "ai", "Review assistant", "Automatic review of meaning submissions; its advice uses the reasoning model."],
  ["text", "ai", "Reasoning model", "The model behind photo import, review advice, completion and pronunciation planning."],
  ["speech", "ai", "Speech generation", "The speech service and one voice per accent."],
  ["background", "ai", "Background policies", "Discovery, concurrency, retries and pauses of background work."],
  ["learning", "learning", "Vocabulary learning", "Answer timing, review scheduling and error-prone words for learner practice."],
  ["learning-connection", "learning", "Learning service connection", "How the learning service reaches this Lexarbor's catalog and sends meaning submissions."],
  ["submissions", "operations", "Meaning submissions", "Where approved meanings are written."],
  ["logging", "operations", "Logging", "The minimum level of the console log."],
  ["telemetry", "operations", "Telemetry", "Optional OpenTelemetry traces and metrics."],
  ["service", "operations", "Service identity", "How this instance names itself in logs."],
] as const

const sectionDefinitions = [
  ["identity", "provider", "Identity provider"],
  ["identity", "hosted-login", "Hosted login application"],
  ["identity", "access", "Access rules"],
  ["rate-limits", "admin-login", "Administrator login"],
  ["network", "forwarding", "Trusted forwarding"],
  ["data-protection", "root-key", "Root key"],
  ["database", "storage", "Connection and startup"],
  ["audio-storage", "connection", "Connection"],
  ["audio-storage", "credentials", "Credentials"],
  ["audio-storage", "advanced", "Upload, playback and recovery limits"],
  ["review", "review", "Review assistant"],
  ["text", "reasoning", "Reasoning model connection"],
  ["speech", "tts", "Speech service"],
  ["speech", "uk", "British English (UK)"],
  ["speech", "us", "American English (US)"],
  ["background", "policy", "Background policies"],
  ["learning", "general", "Answers and schedule"],
  ["learning", "fsrs", "Spaced repetition"],
  ["learning", "error-prone", "Error-prone words"],
  ["learning-connection", "catalog", "Catalog access"],
  ["learning-connection", "integration", "Meaning submissions"],
  ["submissions", "feature", "Submissions"],
  ["logging", "levels", "Log levels"],
  ["telemetry", "traces", "Traces"],
  ["telemetry", "metrics", "Metrics"],
  ["telemetry", "authentication", "Export authentication"],
  ["service", "identity", "Instance"],
] as const

const factDefinitions = [
  ["version", "Application version", "0.0.0-dev", "development channel, revision unknown"],
  ["environment", "Environment", "Testing", null],
  ["container", "Running in a container", "false", null],
  ["httpPort", "HTTP listen port", "5008", "HTTP/1 only"],
  ["serviceName", "Service name", "lexarbor", "Fixed"],
  ["database", "Database", "PostgreSQL", "Host=postgres;Database=lexarbor_test"],
  ["configurationFile", "Configuration file", "data/appsettings.json", "Not created yet"],
  ["administratorSignIn", "Administrator sign-in", "not configured", "/admin/auth/callback"],
  ["enrichmentConfigurationReady", "Enrichment configuration versions", "pending", "Established by an authorized save or an enabled start"],
  ["startedAt", "Process started (UTC)", "2026-11-25T12:37:25Z", null],
] as const

// The server's fixed reasons for the keys a deployment owns, verbatim.
const ownedReasons: Record<string, string> = {
  "AdminAuthentication:Provider": "Removed. Hosted login is the only sign-in, and this key no longer selects one.",
  "AdminAuthentication:HttpTestOrigins": "Removed. The plain-HTTP test transport no longer exists, and this key must stay unset.",
  "DataProtection:RootKey": "The root key protects every administrator session and belongs to the deployment; replacing it signs every administrator out.",
  "DataProtection:RootKeyFile": "The root key protects every administrator session and belongs to the deployment; replacing it signs every administrator out.",
  "ConnectionStrings:Default": "The running process is bound to this database; moving it is an offline operation with the host stopped.",
  "LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION": "The value lives only in the process environment and never enters configuration.",
}
type Definition = [string, string, string, Field['kind'], Field['apply'], Field['value'], number | null, number | null, string[] | null, string | null, string | null, boolean]
type Source = 'default' | 'file' | 'environment' | 'commandLine'
interface Field {
  key: string
  group: string
  section: string
  name: string
  description: string | null
  kind: 'text' | 'url' | 'boolean' | 'triState' | 'integer' | 'number' | 'secret' | 'list' | 'integerList' | 'select'
  apply: 'live' | 'restart' | 'readOnly'
  editable: boolean
  source: Source
  reason: string | null
  value: unknown
  raw: string | null
  /** For a model connection: the key the file still holds it under, when an upgrade left it in an earlier release's section. */
  location: string | null
  hasValue: boolean | null
  /** For a restart-only secret: whether the next start finds one. */
  savedHasValue: boolean | null
  savedValue: unknown
  defaultValue: unknown
  pendingRestart: boolean
  minimum: number | null
  maximum: number | null
  nullable: boolean
  options: string[] | null
  module: string | null
  wire: string | null
}
interface Fact { id: string; name: string; value: string | null; detail: string | null }
export interface Catalog {
  revision: string
  storageAvailable: boolean
  fileExists: boolean
  externallyModified: boolean
  fields: Field[]
  groups: { id: string; area: string; name: string; description: string | null }[]
  sections: { group: string; id: string; name: string }[]
  facts: Fact[]
  unknownFileKeys: string[]
}

export const catalogUrl = '**/admin/system/settings/catalog'
export const startupUrl = '**/admin/system/settings/startup'
export const rateLimitUrl = '**/admin/system/settings/rate-limits'
export const audioUrl = '**/admin/system/settings/audio-storage'
export const enrichmentUrl = '**/admin/enrichment/settings'
export const previewUrl = '**/admin/enrichment/settings/speech-preview'
export const revision = '"1234567890abcdef1234567890abcdef"'
export const nextRevision = '"abcdef1234567890abcdef1234567890"'
export const overridden = 'Overridden by deployment configuration.'

// A fresh deployment as the catalog reports it: image defaults, nothing saved, no file.
export function catalogFixture(): Catalog {
  return {
    revision,
    storageAvailable: true,
    fileExists: false,
    externallyModified: false,
    fields: definitions.map(([key, group, section, kind, apply, value, minimum, maximum, options, module, wire, nullable]) => ({
      key, group, section, name: key, description: null, kind, apply,
      editable: apply !== 'readOnly', source: 'default', reason: apply === 'readOnly' ? ownedReasons[key] ?? null : null,
      // The connection string is reported as its location alone, which a deployment has not given here.
      value: key === 'ConnectionStrings:Default' ? '' : structuredClone(value), raw: null, location: null,
      hasValue: kind === 'secret' ? false : null,
      savedHasValue: null,
      savedValue: apply === 'restart' && kind !== 'secret' ? structuredClone(value) : null,
      defaultValue: structuredClone(value), pendingRestart: false,
      minimum, maximum, nullable, options: options ? [...options] : null, module, wire
    })),
    groups: groupDefinitions.map(([id, area, name, description]) => ({ id, area, name, description })),
    sections: sectionDefinitions.map(([group, id, name]) => ({ group, id, name })),
    facts: factDefinitions.map(([id, name, value, detail]) => ({ id, name, value, detail })),
    unknownFileKeys: []
  }
}

/** The mutable catalog entry of one key. */
export function entry(catalog: Catalog, key: string): Field {
  const field = catalog.fields.find(item => item.key === key)
  if (!field) throw new Error('No catalog entry for ' + key)
  return field
}

export function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
}

/**
 * A signed-in administrator and a catalog endpoint answering `source`: the catalog itself,
 * or a function returning whatever should be answered at the time. A spec registers its own
 * routes afterwards for the endpoints it exercises: the latest matching route answers first.
 */
export async function mockSettings(page: Page, source: unknown | (() => unknown), username = 'ci-admin') {
  const read = typeof source === 'function' ? source as () => unknown : () => source
  await page.route('**/admin/system/settings/catalog', route => route.fulfill({
    status: 200, contentType: 'application/json', headers: { ETag: revision }, body: JSON.stringify(read())
  }))
  await mockAntiforgery(page)
  await page.route('**/admin/auth/session', route => json(route, { username, roles: ['admin'] }))
  await page.route('**/admin/system/version', route => json(route, { version: '1.0.0', revision: null, channel: 'release' }))
  await page.route('**/admin/auth/logout', route => json(route, {}))
  await page.route('**/api/vocabulary-books/all', route => json(route, { success: true, data: { books: [] } }))
}

export const settingsNav = (page: Page) => page.getByRole('navigation', { name: '设置分类' })
export const category = (page: Page, label: string) => settingsNav(page).getByRole('link', { name: label, exact: true })
export const notice = (page: Page) => page.getByRole('status', { name: '配置状态', exact: true })
export const save = (page: Page) => page.getByRole('button', { name: '保存修改', exact: true })
export const reread = (page: Page) => page.getByRole('button', { name: '重新读取', exact: true })
export const unknownState = (page: Page) => page.getByRole('heading', { name: '当前配置状态未知' })
/** One configuration row, by its key. */
export const row = (page: Page, key: string) => page.locator(`[data-setting="${key}"]`)
export const control = (page: Page, label: string) => page.getByLabel(label, { exact: true })
export const switchOf = (page: Page, label: string) => page.getByRole('switch', { name: label, exact: true })
/** The buttons that reveal a credential's replacement input, or mark it for clearing. */
export const replace = (page: Page, label: string) => page.getByRole('button', { name: `${label}：更换`, exact: true })
export const reveal = (page: Page, label: string) => page.getByRole('button', { name: `${label}：设置`, exact: true })
export const clear = (page: Page, label: string) => page.getByRole('button', { name: `${label}：清除`, exact: true })
export const secretsEverywhere = async (page: Page) => ({
  local: await page.evaluate(() => ({ ...localStorage })),
  session: await page.evaluate(() => ({ ...sessionStorage })),
  url: page.url()
})

/** The live module's own answer for whatever the catalog holds for the switch and its numbers. */
export function rateLimitsOf(catalog: Catalog, at = revision) {
  const source = entry(catalog, 'RateLimits:AdminLogin:Enabled')
  return {
    revision: at,
    adminLogin: {
      enabled: source.value, permitLimit: entry(catalog, 'RateLimits:AdminLogin:PermitLimit').value,
      windowSeconds: entry(catalog, 'RateLimits:AdminLogin:WindowSeconds').value, source: source.source, editable: source.editable
    }
  }
}

// The server names a speech protocol from the endpoint: an official OpenAI base speaks the
// compatible way, the official MiniMax bases and an empty address speak the native way.
function official(endpoint: unknown, host: string): boolean {
  try {
    const url = new URL(String(endpoint))
    return url.protocol === 'https:' && url.host === host && url.pathname.replace(/\/+$/, '') === '/v1'
      && !url.username && !url.password && !url.search && !url.hash
  } catch { return false }
}

/**
 * The live module's own answer for whatever the catalog holds for the review assistant and
 * the models, in its nested shape with dotted field names.
 */
export function modelOf(catalog: Catalog, at = revision) {
  const value = (wire: string) => wired(catalog, wire).value
  const client = (wire: string) => ({
    endpoint: value(`${wire}.endpoint`), model: value(`${wire}.model`), hasApiKey: wired(catalog, `${wire}.apiKey`).hasValue,
    timeoutSeconds: value(`${wire}.timeoutSeconds`)
  })
  const voice = (locale: 'uk' | 'us') => ({
    voice: value(`enrichment.tts.${locale}.voice`), speed: value(`enrichment.tts.${locale}.speed`),
    style: value(`enrichment.tts.${locale}.style`), emotion: value(`enrichment.tts.${locale}.emotion`),
    pitch: value(`enrichment.tts.${locale}.pitch`), volume: value(`enrichment.tts.${locale}.volume`)
  })
  const configured = (client: { endpoint: unknown; model: unknown; hasApiKey: boolean | null }) =>
    client.endpoint !== '' && client.model !== '' && client.hasApiKey === true
  const reasoning = client('enrichment.text')
  const speech = client('enrichment.tts')
  const provider = official(speech.endpoint, 'api.openai.com') ? 'OpenAICompatible'
    : !speech.endpoint || official(speech.endpoint, 'api.minimax.io') || official(speech.endpoint, 'api.minimax.cn') ? 'MiniMaxNative' : 'Unsupported'
  const fields: Record<string, unknown> = {}
  for (const item of catalog.fields) {
    if (item.module !== 'enrichment' || !item.wire) continue
    fields[item.wire] = { source: item.source, editable: item.editable, reason: item.reason }
  }
  // The previous settings API names remain aliases of the reasoning connection.
  for (const name of ['endpoint', 'model', 'apiKey', 'timeoutSeconds']) fields[`reviewAssistant.${name}`] = fields[`enrichment.text.${name}`]
  // Capability indicators older clients read; none is a configuration field.
  const derived = 'Determined by model configuration.'
  Object.assign(fields, {
    'enrichment.fieldsEnabled': { source: 'default', editable: false, reason: derived },
    'enrichment.audioEnabled': { source: 'default', editable: false, reason: derived },
    'enrichment.tts.provider': { source: 'default', editable: false, reason: 'Determined by the speech endpoint.' }
  })
  return {
    revision: at, configurationReady: false,
    reviewAssistant: { ...reasoning, autoReviewEnabled: value('reviewAssistant.autoReviewEnabled') },
    enrichment: {
      fieldsEnabled: configured(reasoning), audioEnabled: configured(reasoning) && configured(speech),
      text: reasoning,
      tts: { ...speech, provider, uk: voice('uk'), us: voice('us') },
      concurrency: value('enrichment.concurrency'), failureBudget: value('enrichment.failureBudget'),
      serviceFailureThreshold: value('enrichment.serviceFailureThreshold'),
      servicePauseSeconds: value('enrichment.servicePauseSeconds'), maxServicePauseSeconds: value('enrichment.maxServicePauseSeconds'),
      textConcurrency: value('enrichment.textConcurrency'), throttleInitialSeconds: value('enrichment.throttleInitialSeconds'),
      throttleMaxSeconds: value('enrichment.throttleMaxSeconds'), discoveryBatchSize: value('enrichment.discoveryBatchSize'),
      scanIntervalSeconds: value('enrichment.scanIntervalSeconds')
    },
    fields
  }
}

/** The catalog entry of a live key, by its name in its module's request body. */
function wired(catalog: Catalog, wire: string): Field {
  // The review assistant's connection names remain aliases of the reasoning model's.
  const field = catalog.fields.find(item => item.wire === wire)
    ?? catalog.fields.find(item => item.wire === wire.replace(/^reviewAssistant\./, 'enrichment.text.'))
  if (!field) throw new Error('No catalog entry for ' + wire)
  return field
}

/**
 * Applies what the review and enrichment module committed to the catalog the next read
 * answers: `body` is that module's nested request shape with dotted names.
 */
export function applied(catalog: Catalog, body: Record<string, unknown>) {
  const visit = (node: object, prefix: string) => {
    for (const [name, value] of Object.entries(node)) {
      const wire = prefix ? `${prefix}.${name}` : name
      // A plain object that is not a credential action is a section: only its leaves are keys.
      if (value && typeof value === 'object' && !('action' in value)) {
        visit(value, wire)
        continue
      }
      const field = wired(catalog, wire)
      field.source = 'file'
      if (value && typeof value === 'object') field.hasValue = (value as { action: string }).action !== 'clear'
      else field.value = value
    }
  }
  visit(body, '')
}
