# System settings

The administrator page at `/#/settings` displays effective request ceilings, their
configuration source, and separate unsaved switch drafts. A failed read means
**unknown**, never disabled. Numeric limits and windows are read-only; change them
offline and restart. The audio storage API below is available; its administrator page is a separate feature.

## Request rate limits

`GET /admin/system/settings/rate-limits` returns the following JSON directly
(without a vocabulary response envelope):

```json
{
  "revision": "\"0123456789abcdef0123456789abcdef\"",
  "adminLogin": {
    "enabled": false,
    "permitLimit": 10,
    "windowSeconds": 300,
    "source": "default",
    "editable": true
  },
  "publicApi": {
    "enabled": false,
    "permitLimit": 300,
    "windowSeconds": 60,
    "source": "default",
    "editable": true
  }
}
```

`ETag` equals the opaque `revision`, including its quotes. `source` describes the
switch: `default`, `file`, `environment`, or `commandLine`. The latter two sources
have `editable: false`; the other switch may still be saved independently. The
numeric values are the effective startup configuration, including invalid values
when disabled. A disabled policy with invalid numbers cannot be enabled online.

`PUT /admin/system/settings/rate-limits` accepts at least one boolean field:

```json
{ "adminLoginEnabled": true, "publicApiEnabled": false }
```

Omitted switches retain their effective values. Unknown or duplicate fields,
non-booleans, and an empty object are rejected. Supply the current **strong**
`If-Match` value exactly as returned by GET. A successful PUT returns the same
shape as GET and an ETag for that response snapshot. All setting reads and writes
require the existing administrator authorization; anonymous callers receive 401
and non-administrators 403. Session-cookie PUTs require `X-SignaCore-CSRF` and its
antiforgery cookie. An explicit Bearer header takes precedence over a cookie;
an invalid Bearer token cannot fall back to a valid session. Setting responses,
including errors, are `Cache-Control: no-store` and carry the administrator
security headers. Settings APIs do not consume a rate-limit budget.

| Status | Result |
|---|---|
| 200 | File committed and runtime switches published; GET confirms current state |
| 400 | Invalid JSON/fields, malformed or weak If-Match, or invalid enabled quota/window |
| 409 | Requested switch is overridden by environment/command-line configuration |
| 412 | Stale revision, concurrent save, or offline disk edit; refresh before retrying |
| 413 | Payload exceeds 4096 bytes |
| 428 | Missing If-Match |
| 503 | Configuration cannot be safely written; old runtime state remains active |

Failures use fixed English `message` text, with safe field names on an override
conflict. They never echo input. Versions are shared across all setting modules;
a save in another module invalidates an older settings page.

## Persistence and runtime behavior

Both ceilings default to disabled. Enabling uses the existing ServiceMantle
named policy and per-client-address sliding-window bucket. Publishing a change
applies to requests that enter the limit pipeline afterwards; an in-flight
request keeps its decision. Disabling skips the policy, consumes no permits,
and does not add that policy's Retry-After. Re-enabling retains a still-live
bucket and does not reset consumed permits. Login and public API budgets remain
independent. Only `/admin/auth/start` and `/api/*` are metered; unknown public
routes, browsing, question batches, and audio playback share the public budget.
Callback, logout, health, SPA, administrator business, and settings routes remain
unmetered. Audio upload concurrency/queue admission is a separate unchanged guard.

A save modifies only allowed settings in `<contentRoot>/data/appsettings.json`
(`/app/data/appsettings.json` in containers), preserving unrelated JSON fields.
The shared coordinator creates missing directories/files, writes a same-directory
temporary file, flushes it, atomically replaces the target, then publishes one
immutable runtime snapshot. The source template is never overwritten. Unix files
are created as 0600 and new data directories as 0700. Read-only files/directories,
unsafe links, malformed JSON, and write failures keep the old file and runtime;
GET does not create the file. Restart loads the saved switches with the existing
precedence: defaults < persisted file < environment < command line. Offline
edits require restart; online file editing concurrent with page saves is unsupported.

The page disables duplicate submission, keeps nonsecret drafts after failures,
and reads back after saving. A lost PUT response may mean the file was committed:
it displays an unknown outcome and performs GET, never automatically resubmits.
If confirmation fails, retry the read and compare the current state with the draft.
Leaving the page or logging out aborts reads and discards drafts; no draft is
stored in browser storage or a URL. The exact `/settings` return target is accepted
by hosted login; external URLs and encoded variants remain rejected.

These are process-local request ceilings, not distributed quotas or DDoS protection.
Configure trusted proxies correctly and respect 429 Retry-After guidance; see
[Deployment](../development/Deployment.md#rate-limits-and-client-addresses).

## Audio storage (S3)

`GET /admin/system/settings/audio-storage` returns effective values directly:

```json
{
  "revision": "\"0123456789abcdef0123456789abcdef\"",
  "enabled": false,
  "internalEndpoint": "",
  "publicEndpoint": "",
  "region": "us-east-1",
  "bucket": "lexarbor-audio",
  "keyPrefix": "audio/",
  "hasAccessKey": false,
  "hasSecretKey": false,
  "fields": {
    "enabled": { "source": "default", "editable": true, "reason": null },
    "internalEndpoint": { "source": "default", "editable": true, "reason": null },
    "publicEndpoint": { "source": "default", "editable": true, "reason": null },
    "region": { "source": "default", "editable": true, "reason": null },
    "bucket": { "source": "default", "editable": true, "reason": null },
    "keyPrefix": { "source": "default", "editable": true, "reason": null },
    "accessKey": { "source": "default", "editable": true, "reason": null },
    "secretKey": { "source": "default", "editable": true, "reason": null }
  }
}
```

The same administrator, Cookie/CSRF, Bearer precedence, security headers, no-store,
opaque ETag and shared-file revision rules apply. A missing file does not prevent
GET or first configuration. Credential values are never returned, including on
successful PUT; only their presence and source are exposed. `fields` contains the
eight names shown above. Sources are `default`, `file`, `environment`, and
`commandLine`. Deployment overrides have `editable: false` and the fixed safe
reason `Overridden by deployment configuration.`. Advanced upload, queue, timeout,
recovery and signature TTL parameters are not accepted by this API; change them
offline and restart.

`PUT /admin/system/settings/audio-storage` requires the current strong `If-Match`
and at least one permitted field or credential operation. For example:

```json
{
  "enabled": true,
  "internalEndpoint": "http://seaweedfs:8333/",
  "publicEndpoint": "https://audio.example.com/s3/",
  "region": "us-east-1",
  "bucket": "lexarbor-audio",
  "keyPrefix": "audio/",
  "accessKey": { "action": "replace", "value": "<new access key>" },
  "secretKey": { "action": "replace", "value": "<new secret key>" }
}
```

Omitted fields retain effective values. Each credential independently accepts
`{"action":"keep"}`, `{"action":"replace","value":"<nonblank new value>"}`,
or `{"action":"clear"}`. Omission means keep. Keep and clear must not include a
`value`, including null or an empty string; replace requires a nonblank string.
Unknown names, duplicates, wrong types, invalid actions and an empty object return
400 without echoing input. Do not submit a masked placeholder as a replacement.
An explicit keep is allowed for an overridden credential; attempting to replace
or clear it returns 409. Other overridden fields are likewise rejected, even
when the submitted value equals the effective one. The 409 override response
contains only a fixed message and safe requested field names.

Disabled configurations can be incomplete. Enabling requires a complete locally
valid candidate, including credentials, endpoints, destination and unchanged
advanced parameters. Saving performs no remote probe, bucket listing or creation:
an offline S3 endpoint or credentials without remote permission can still be
saved. Verify the deployment through actual upload and playback.

A successful PUT returns the GET shape and its ETag after atomic file commit and
runtime publication. Editable retained audio fields are included in the persisted
candidate, preserving credentials even on a first save without a template or after
file loss. Environment/command-line values are not copied into the file. Unrelated
RateLimits, OIDC and unknown JSON fields retain their values. Current runtime
clients are replaced only after successful commit; rejected candidates leave the
file and existing operations intact.

| Status | Result |
|---|---|
| 200 | Complete candidate committed and published immediately |
| 400 | Invalid body, If-Match, enabled configuration, or local client construction |
| 409 | Requested deployment override, or destination change with any audio asset/upload record or active operation |
| 412 | Stale shared revision, concurrent save, or offline disk edit |
| 413 | Body exceeds 16384 bytes, including streamed requests |
| 428 | Missing If-Match |
| 503 | Unsafe/unavailable configuration storage; previous runtime remains active |

InternalEndpoint, Bucket and KeyPrefix changes require no asset/upload records
and no active audio operations, including retired client generations. Existing
history needs a separate migration; unbinding does not remove this protection.
Credential, region, public endpoint and enabled changes keep old operations on
their captured generation. Disabling blocks new audio operations with the existing
503 behavior while metadata remains readable; re-enabling wakes bounded recovery.
Keep old remote credentials valid until actual old IO ends. Existing signatures
are not immediately revoked. Public audio routes and response fields are unchanged.

Do not log request bodies or store credential drafts in browser storage, URLs or
telemetry. The default Host HTTP logging categories are suppressed; settings errors
never expose paths, original input or SDK exception details. After a lost PUT
response, GET to determine the current state; do not automatically resend the
credential-bearing request. See [Audio API](Audio.md) and
[Deployment](../development/Deployment.md#optional-audio-object-storage) for operational details.

## Review assistant and enrichment

`GET /admin/enrichment/settings` and conditional `PUT` use the same administrator,
Cookie/CSRF, Bearer precedence, shared strong ETag, no-store and atomic file rules
above. Integration/submission tokens have no access. The response has `revision`,
`configurationReady`, `reviewAssistant`, `enrichment`, and a `fields` dictionary.
Dictionary keys use dotted names, such as `enrichment.text.model`; each entry has
`source`, `editable` and a safe override `reason`. Credential fields return only
`hasApiKey`, never `apiKey` or private version metadata. For example, a partial PUT:

```json
{
  "reviewAssistant": {
    "endpoint": "https://review.example.com/v1",
    "model": "review-model",
    "apiKey": { "action": "replace", "value": "<review key>" }
  },
  "enrichment": {
    "fieldsEnabled": false,
    "audioEnabled": true,
    "text": {
      "endpoint": "https://planner.example.com/v1",
      "model": "planner-model",
      "apiKey": { "action": "replace", "value": "<planner key>" }
    },
    "tts": {
      "provider": "MiniMaxNative",
      "endpoint": "https://tts.example.com/v1",
      "model": "speech-2.8-hd",
      "apiKey": { "action": "replace", "value": "<speech key>" },
      "uk": { "voice": "<supported UK voice>", "speed": 1 },
      "us": { "voice": "", "speed": null }
    }
  }
}
```

All three client objects accept `endpoint`, `model`, credential `apiKey`, and
`timeoutSeconds` (1–300, default 30). Review settings retain the existing
`MeaningSubmissions:Ai` keys and enable advice when endpoint and model are set;
clearing endpoint or model disables advice. Enrichment uses the separate
`Enrichment` section. Both enrichment switches default to false. Audio-only mode
still requires the text client's pronunciation preparation and never implies
permission to write text fields. Each locale has its own voice and nullable speed;
both default to empty. Incomplete configurations can be saved: a missing UK setup
does not invalidate the US setup. Speed accepts 0.1–4 as a configuration bound;
the actual provider/model adapter must enforce its narrower supported range and
pronunciation capabilities. Saving configuration never calls a supplier. This
foundation does not start an enrichment worker, create tasks or generate audio.

Providers are `MiniMaxNative` (default model `speech-2.8-hd`) and
`OpenAICompatible`. A protocol/provider name does not promise support for arbitrary
models, voices or pronunciation instructions. There is no bypass-preparation or
ignore-pronunciation switch. Unsupported combinations must stop safely in the
provider adapter rather than send an unconstrained request.

The internal pronunciation preparation client uses only `Enrichment:Text`, independently
of the review assistant. It posts to `<endpoint>/chat/completions` with JSON mode
(`response_format.type=json_object`) and validates every field locally; compatibility
does not imply JSON Schema support. See the [preparation contract](Audio.md#pronunciation-preparation)
for bounds, explicit targets and safe failure results. Registration alone starts no
calls, worker or TTS. Audio-only mode runs preparation with no writable field candidates.

Additional `enrichment` fields and defaults:

| Field | Default | Allowed |
|---|---|---|
| `concurrency` | 1 | 1–8 |
| `failureBudget` | 5 | 1–20 |
| `serviceFailureThreshold` | 5 | 1–20 |
| `servicePauseSeconds` / `maxServicePauseSeconds` | 60 / 600 | 1–600 / 1–3600; maximum >= initial |
| `throttleInitialSeconds` / `throttleMaxSeconds` | 8 / 120 | 1–120 / 1–3600; maximum >= initial |
| `discoveryBatchSize` | 20 | 1–1000 |
| `scanIntervalSeconds` | 30 | 1–3600 |

Omitted fields and credentials are retained. Each `apiKey` supports exactly
keep/replace/clear as in the S3 API. Deployment overrides remain read-only, including
attempts to submit their current value; explicit credential keep is permitted.
GET never creates a file. PUT accepts at least one known field or credential
operation, with a 16384-byte body limit, a maximum JSON depth of 8, strict types and
no duplicate/unknown names. Repeating the same setting through nested and dotted
property names returns 400, including credential `keep` operations; rejection
leaves the file, runtime snapshot, configuration versions and ETag unchanged.
Endpoints must be absolute HTTP(S) URLs without user
information, query or fragment. Errors are fixed English text and never echo input.
Status codes are 200, 400, 409, 412, 413, 428 and 503 with the same meanings as the
S3 settings API, except no storage-destination migration check is involved.

Each operation captures one immutable published snapshot. A successful save affects
subsequent review operations and enrichment consumers without restart; an in-flight
operation retains its model/key snapshot. File, parsing, candidate, cancellation
before commit or concurrency failures publish nothing. After the atomic commit,
publication completes even if the response is lost; GET before deciding to resave.

The HTTP ETag is process-local and must never be used as a task configuration
version. Internal text/UK-TTS/US-TTS random versions survive restart. Only relevant
client settings advance their step/locale version; review settings, enable flags,
scan controls and retry/service controls do not reset task failure budgets. A
shared TTS provider/model/endpoint/key change affects both locales; a voice/speed
change affects only its locale. Private keyed comparison metadata is atomically
stored with the configuration at 0600; keys and comparison digests are not exposed
in responses or task history. Older files with no metadata still start, but
`configurationReady` is false until startup initialization or an authorized save
establishes it. Before workers start, an enabled deployment atomically initializes
missing/changed metadata without calling suppliers. Disabled legacy deployments do
not create a file. An effective deployment override change invalidates only affected
versions and enabled startup persists their replacements; unwritable metadata keeps
generation waiting. Retain the
whole configuration file, not just visible fields, across container replacement.
