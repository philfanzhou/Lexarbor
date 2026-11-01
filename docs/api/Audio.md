# Administrator audio queries, uploads, logical deletion and playback

Audio queries use the existing administrator policy: an administrator Bearer token for external programs, or the hosted-login session cookie for the administration UI. A supplied Bearer header takes precedence over a cookie. These GET requests do not require a CSRF header. Keep administrator credentials out of browser application code. Existing security headers, roles and rate-limit policies are unchanged.

The administrator API provides metadata, conditional MP3 uploads, conditional logical deletion and short-lived playback links. It does not generate audio or claim generation jobs. Metadata queries do not inspect S3. Audio is disabled by default; missing `Audio` configuration is supported. Every successful metadata query includes `enabled`, reflecting the runtime configuration acquired once at request start. Metadata remains readable when disabled, but callers must not start audio work when `enabled` is `false`. See [deployment configuration](../development/Deployment.md#optional-audio-object-storage).

Conditional uploads use the internal Host runtime configuration manager. One
operation reference covers multipart parsing limits, domain reservation parameters
and the matching object client. Enabling through the administrator settings API
after an initially disabled startup permits uploads; disabling rejects new uploads with 503 while requests
already started keep their old generation. Credential/PublicEndpoint rotation
changes subsequent uploads, and the shared host admission quota remains unchanged.
Cancellation/timeout releases the request reference; a provider still finishing an
ignored cancellation retains its client until actual completion.

The internal destination guard rejects changes to InternalEndpoint/Bucket/KeyPrefix
when any asset/upload record or active operation exists, including unbound history
and cleanup state. It serializes first operation acquisition with destination
commit, without holding the state lock during S3 or database IO. The protected
[S3 settings API](SystemSettings.md#audio-storage-s3) maps this guard to 409.
Background recovery waits while disabled, including after
initially disabled startup. A saved enablement wakes a bounded batch. Each batch
holds one configuration/client generation; disabling prevents new batches while
an already started batch finishes on its old generation. Delete/Stat providers
that ignore cancellation retain their client until actual completion. Recovery
keeps the existing database lease, retry and never-published-only cleanup rules.

## Read-only pronunciation metadata

`GET /api/vocabulary/{wordId}?bookId=...` includes `pronunciation` in each
`meanings` item. Book/unit `GET /api/vocabulary-books/{bookId}/entries` items
and integration [catalog search](MeaningSubmissions.md#get-integrationvocabularysearchkeyword)
items carry the same projection:

```json
{
  "uk": { "phonetic": "/specific-uk/", "source": "meaning" },
  "us": { "phonetic": "/default-us/", "source": "default" }
}
```

UK and US are independent. A nonblank meaning-specific value is returned as
stored with source `meaning`; otherwise the word default is used with source
`default`. An empty effective value is `phonetic: null` and keeps source
`default`. Existing `phoneticUk`/`phoneticUs` fields still contain the word
defaults, including their existing null/string shape. No task, provider or
credential metadata is included.

This source describes the displayed phonetic, independently of audio
availability. An explicit value equivalent to the default still has source
`meaning`, even when playback falls back to source `default`. A different value
without its own audio remains `missing`. Read the projection for display and
request playback with the same `meaningId`; do not assume a nonempty phonetic
means audio is ready. Without `meaningId`, playback still selects only the word
default.

`pronunciation` is output-only. Single/batch imports and submission approval do
not write meaning-specific phonetics, and importing an existing meaning does
not clear them. Echoing the projection or undeclared meaning phonetic fields
does not grant editing permission. This slice adds no pronunciation editor,
generation, schema migration or deployment setting; the existing schema-version
gate and backup/restore policy continue to apply.

## Generation targets

`GET /admin/audio-targets?bookId=...&locale=en-GB&status=missing&page=1&size=20`

All query parameters are optional:

| Parameter | Values and meaning |
| --- | --- |
| `bookId` | Restrict default targets to words with a meaning in this book, and override targets to meanings in this book. Disabled books are readable. Omitting it includes words without any book. An unknown book returns 404. |
| `locale` | Exactly `en-GB` or `en-US`; omit it for both. |
| `status` | Exactly `missing`, `ready`, or `stale`; omit it for all content states. |
| `page` | One-based page number, default 1. |
| `size` | Page size, default 20, from 1 to 100. |

Blank filter values, unsupported locale/status values, nonpositive page/size, oversized pages and offset overflow return 400. The list contains two default targets per word and only the accents of **existing** meaning overrides. It never creates missing override targets for every meaning. Results sort by target type (default before meaning), target ID (word ID or meaning ID, SQLite binary ordering), then locale (`en-GB` before `en-US`). Filtering applies before pagination. `totalCount` and `totalPage` describe the filtered result; an empty result has `totalPage: 0`. A page beyond the last page returns an empty `items` array with the current totals.

Each request reads one consistent SQLite snapshot, including inputs, bindings, states and totals. Different pages are independent snapshots, not a frozen export or a job-claim queue. A generation program should rescan, handle changes and use the target's current version and own binding as upload conditions for uploads. Computing content-state filters scans matching targets in bounded batches; page size bounds the response, not the number of inspected targets.

Example response for a word with no audio:

```json
{
  "success": true,
  "data": {
    "enabled": false,
    "items": [
      {
        "wordId": "word-id",
        "meaningId": null,
        "locale": "en-GB",
        "targetType": "default",
        "status": "missing",
        "source": null,
        "sourceRevision": "<64 lowercase hexadecimal characters>",
        "input": {
          "word": "Apple",
          "phonetic": "ˈæpəl",
          "partOfSpeech": null,
          "meaning": null
        },
        "ownBinding": null,
        "effectiveBinding": null
      }
    ],
    "totalCount": 1,
    "totalPage": 1
  }
}
```

This example is a filtered one-target page (for example, with `locale=en-GB`). Default inputs preserve the displayed spelling and include only the requested accent's phonetic. Meaning inputs use that accent's nonblank meaning-specific phonetic when present, otherwise the word default, and additionally include part of speech and definition. Examples, textbook enablement, positions and timestamps do not affect the version. Revisions are opaque, deterministic SHA-256 values computed from the versioned spoken input: edits and batch imports both affect them without separate invalidation writes. Default targets and meanings with a null/blank specific phonetic retain their previous input encoding and exact revision.

## Word and first-override metadata

`GET /admin/vocabulary/{wordId}/audio?meaningId=...`

Omit `meaningId` for defaults; supply an owned meaning ID to inspect an existing or first override. A blank meaning ID returns 400; an unknown word, unknown meaning or a meaning belonging to another word returns 404. Disabled books and words without a book remain readable. Success returns `{ "success": true, "data": { "enabled": false, "items": [...] } }`, with exactly two entries in `en-GB`, `en-US` order and the same target fields as the list.

`sourceRevision` always describes the requested target's **current** generation input. `ownBinding` is the target's own stored binding, or null. `effectiveBinding` is the audio selected for listening, or null. Each binding contains only `assetId` and its stored `sourceRevision`; internal bucket, object key, endpoints, credentials and signed URLs never appear.

For a first meaning override, `ownBinding` is null even when `effectiveBinding` points at the word's default audio. Use the own binding's asset ID as the upload's `expectedAssetId`; use an empty expected asset when own binding is null. Never substitute the fallback asset ID. The fallback's version is compared with current **default** input, while the returned target version describes the meaning input.

`source` is `default`, `meaning`, or null when missing. Selection follows the rules below. `missing`, `ready` and `stale` describe the selected binding's content version. They do not prove the remote object exists or is playable, because queries make no S3 calls.

### Meaning-specific pronunciation selection

UK and US are evaluated independently by metadata and both playback APIs:

| Request and explicit phonetic | Selected audio |
| --- | --- |
| No `meaningId` | The requested accent's default only. |
| Meaning-specific phonetic is null or whitespace | The default only, even if a legacy own binding exists. |
| Nonblank specific phonetic and an own binding exists | The own binding, including its ordinary stale state. |
| Nonblank specific phonetic, no own binding, equivalent nonempty default phonetic | The same-accent default, if it exists. |
| Nonblank specific phonetic, no own binding, different or absent default phonetic | `missing`; a known different pronunciation is never substituted. |

An absent selected binding is `missing`; accents never fall back to each other.
For equivalence only, each value is normalized to Unicode NFC and trimmed, one
matching outer `/.../` or `[...]` pair is removed, then the interior is trimmed.
Both results must be nonempty and equal with ordinal comparison. Case, internal
spaces, stress, phonemes and vowel length remain significant; no AI comparison
or compatibility normalization is used. This comparison does not rewrite stored
values or normalize revision input: a nonblank specific value enters the revision
exactly as stored and affects only that meaning and accent.

The `AddMeaningPhonetics` upgrade initializes both specific fields to null. It
preserves legacy assets, bindings, receipts and revisions, but intentionally stops
playing legacy overrides until an explicit specific phonetic exists. Such a
binding remains visible as `ownBinding` for conditional upload and logical delete;
`effectiveBinding` may be the default or null. This upgrade introduces no public
phonetic write fields, administrator editor or automatic generation.

## Errors and cancellation

Success uses the existing `{ "success": true, "data": ... }` envelope. Business failures use `{ "success": false, "message": "<fixed English message>" }`. Missing/invalid administrator identity returns 401, an authenticated non-administrator returns 403, invalid query values return 400, and missing resources or wrong meaning ownership return 404. Framework-level parsing errors retain the host's existing handling. Query cancellation releases the read transaction without creating assets, bindings or upload operations. Existing vocabulary endpoints and JSON contracts are unchanged.

## Conditional MP3 upload

`PUT /admin/vocabulary/{wordId}/audio/{locale}?meaningId=...`

Use an administrator Bearer token over HTTPS. The existing administrator session also works with its CSRF cookie and `X-SignaCore-CSRF` header; Bearer requests do not require CSRF even when a session cookie is present. Locale must be exactly `en-GB` or `en-US`. Omit `meaningId` for the word default, or supply an owned meaning for an override. Disabled books and words without a book are allowed.

Send a UUID in the `Idempotency-Key` header and `multipart/form-data` containing exactly one file part named `file` and these fields:

| Field | Contract |
| --- | --- |
| `sourceRevision` | Required current target revision, 64 lowercase hexadecimal characters, from the metadata query. |
| `expectedAssetId` | Required field: empty for no **own** binding; otherwise the own binding's canonical lowercase UUID. Never use a fallback default asset for a new meaning override. |
| `producer`, `voice` | Optional generation labels, at most 128 characters each, with no control characters. They do not validate pronunciation or voice quality. |

Duplicate or unknown fields, additional files and malformed multipart bodies are rejected. File names, extensions and declared MIME do not establish validity: the bytes must contain at least two complete MPEG Layer III frames (optional bounded ID3 tags are accepted). Truncated frames and other formats return 415. This HTTP endpoint does not generate speech or transcode WAV/Opus; an external uploader must supply a valid MP3 from the current input. The separate internal speech adapters described below do not change the manual upload contract.

Success returns `{ "success": true, "data": { "assetId": "<UUID>", "sourceRevision": "<revision>" } }`, without a URL or internal object location. Uploads create fresh objects, never overwrite an existing object. The default and each meaning override, and the two accents, have independent conditions.

The same key and identical content, target, conditions and generation labels replay the original immutable receipt without uploading again or changing the binding. This still applies after later edits, replacements or target deletion. A changed fingerprint or a still-processing operation returns 409. A failed operation replays its original 404/409/503 result; use a **new** key for a new attempt. Authentication, multipart validity and fingerprint checks precede replay. Keep the same key when retrying a lost response; do not assume a lost response means the database rolled back.

New uploads require both the current revision and own asset to match before reserving and again after S3 completes. Competing replacements publish at most one binding. Edits/imports/deletion during transfer prevent publication and return 409 or 404. Existing audio is retained on a failed replacement. Source changes mark retained audio stale through the existing query rules.

The file limit is `Audio:MaxUploadBytes` (default 5 MiB, configurable up to 20 MiB), plus at most 64 KiB total multipart overhead. After authorization, dedicated per-host admission allows `Audio:MaxConcurrentUploads` active uploads (default 2) and `Audio:MaxQueuedUploads` waiting requests (default 8). Admission precedes body consumption; a full queue or admission wait timeout returns 429 with `Retry-After: 1`. Body reading, admission waiting and S3 transfer each have the configured `Audio:OperationTimeoutSeconds` bound (default 60 seconds). These limits apply only to this audio route and do not change existing global limits.

Storage disabled, storage failure and upload timeout return safe 503 messages; invalid conditions/header/fields return 400, missing target/ownership returns 404, and oversized content returns 413. Storage exceptions, credentials, object keys and signed URLs are not returned. A cancellation before reservation causes no upload; cancellation after reservation retains a durable failure or expiring operation. S3 IO holds no SQLite writer transaction, and only a valid five-minute lease can publish. Known unpublished keys remain recorded for the separate recovery task; this slice does not automatically remove them. Already published objects remain available for database backups after replacement or logical deletion. See [backup and storage configuration](../development/Deployment.md#optional-audio-object-storage).

## Conditional logical deletion

`DELETE /admin/vocabulary/{wordId}/audio/{locale}?meaningId=...`

Use the same administrator authorization, Cookie CSRF and Bearer precedence as uploads. Locale must be exactly `en-GB` or `en-US`. Omit `meaningId` for the default binding; supply an owned meaning ID to remove an override. Disabled books and words without a book are allowed.

Send exactly one strong, quoted entity tag in `If-Match`, containing the current **own binding** asset's canonical lowercase UUID, for example `If-Match: "12345678-1234-1234-1234-123456789abc"`. Read `ownBinding` from metadata first. Do not use the default `effectiveBinding` to delete a meaning with no override. Bare UUIDs, weak tags, wildcards, lists and multiple header values return 400. Missing or invalid conditions, unsupported locale and blank meaning IDs also return 400.

Logical deletion acquires one runtime reference before validation and keeps it
through the conditional database operation. Internal enablement permits new
deletes even after disabled startup; closing audio rejects new deletes with 503,
while a delete already started keeps its original enabled decision. Metadata
remains readable while disabled; a query spanning a configuration change reports
its own captured `enabled`. Neither path calls S3 or removes asset/upload history.

Success is `{ "success": true, "data": { "removed": true } }`. Only the selected binding is removed. Deleting a meaning override re-evaluates the selection rules above: a different explicit pronunciation without its own audio becomes `missing`; inheritance or an equivalent nonempty default can use the default. Deleting a default leaves existing overrides and the other accent untouched. A missing target, wrong meaning ownership or absent own binding returns 404 (including a repeated delete); a different current own asset returns 409. On 409, reread metadata before deciding whether to delete the newer binding.

Deletion competes with replacements in one short SQLite write transaction. If replacement wins, the old `If-Match` cannot remove its new binding. If deletion wins while replacement is transferring, that replacement's old-asset condition prevents publication. Cancellation before write admission changes nothing; once the short write is admitted it finishes under the existing commit semantics, even if the response is lost.

Audio disabled returns fixed 503. Deletion makes no S3 calls and retains all assets, objects, publication timestamps, upload operations and immutable idempotency receipts. Replaying an earlier successful upload returns its original receipt without restoring the deleted binding. Previously issued playback links remain usable until expiry. Internal object locations and storage credentials never appear in delete responses. No schema or deployment configuration change is required.

## Administrator playback

`GET /admin/vocabulary/{wordId}/audio/{locale}/playback?meaningId=...`

An administrator can listen to words in disabled books and words with no book. `meaningId`, when supplied, must belong to the requested word; wrong ownership or an absent target returns 404. The locale must be exactly `en-GB` or `en-US`; invalid locale or blank meaning ID returns 400. The existing administrator role and Bearer-over-cookie priority apply. GET requires no CSRF token and keeps existing rate-limit policies. Audio disabled returns `503` with `Audio storage is disabled.`.

Selection uses one completed database read snapshot and the [meaning-specific rules](#meaning-specific-pronunciation-selection). Storage Stat and signing run after that snapshot has ended, with request cancellation and a bounded combined deadline (`Audio:OperationTimeoutSeconds`, default 60 seconds). They hold no SQLite write transaction or global write lock. A concurrent replacement can leave the link pointing to the snapshot's retained old asset, which is valid until its expiry.

A successful response is `{success:true,data:...}` with these fields:

| Field | Meaning |
|---|---|
| `locale` | Requested accent |
| `status` | `missing`, `ready` or `stale` content version |
| `source` | `default`, `meaning` or null |
| `assetId` | Selected asset ID, or null |
| `sourceRevision` | Selected binding's stored revision, or null; current generation input remains available through metadata |
| `url` | Short-lived signed GET URL, or null |
| `expiresAt` | UTC expiry of the signed URL, or null |

Missing audio is a successful `missing` response with null source, asset, revision, URL and expiry; it does not access S3. Stale audio still receives a link for the selected old asset. If that object is lost, denied or storage times out, the response is fixed `503` with `Audio storage is unavailable.`; it never silently signs a different default. Provider errors and internal location/configuration fields are omitted. There are no database mutations or migrations.

Playback responses use `Cache-Control: no-store`. The URL points to the configured public S3 endpoint and contains the object path and signature needed to download; it is a temporary playback credential. Do not log it, send it to analytics or persist it. Internal S3 endpoints, separate bucket/key fields and storage credentials are never returned. Link lifetime uses `Audio:PlaybackTtlSeconds` (default 900, allowed 60–3600). Request a fresh link when the user clicks play or when a link expires. Previously issued links cannot be immediately revoked by deleting a binding, replacing audio or disabling a book; they may remain usable until expiry. Object availability can still change after Stat. The caller handles missing/stale/503 and normal network or decoding failures.

## Public book-authorized playback

`GET /api/vocabulary/{wordId}/audio?bookId=...&meaningId=...&locale=en-GB`

This anonymous endpoint uses the existing public API rate limit and `Retry-After` on 429. It requires `bookId`; omit `locale` to request both `en-GB` and `en-US`, or specify exactly one. A supplied `meaningId` must belong to both this word and this book. Without a meaning ID, the word must have at least one meaning in this book. Missing/blank book ID, invalid locale or blank meaning ID returns 400; missing book/word/meaning or membership returns 404; a disabled book returns the existing business-rule 422. A word belonging only to another disabled book cannot be requested through an enabled book. Audio disabled returns safe 503. Existing public fields and authentication remain compatible; detail and entry responses add only the read-only pronunciation metadata described above.

An enabled book, target membership, current inputs and both accent bindings are read in one SQLite snapshot. Selection follows the same [meaning-specific rules](#meaning-specific-pronunciation-selection), including inheritance for blank specific phonetics and `missing` for a different explicit pronunciation without its own audio. Responses contain `data.items` in British/American order (one item when locale is specified), with the same fields as administrator playback:

```json
{
  "success": true,
  "data": {
    "items": [
      { "locale": "en-GB", "status": "ready", "source": "default", "assetId": "asset-id", "sourceRevision": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "url": "https://audio.example.com/temporary-signed-url", "expiresAt": "2026-10-12T06:00:00+00:00" },
      { "locale": "en-US", "status": "missing", "source": null, "assetId": null, "sourceRevision": null, "url": null, "expiresAt": null }
    ]
  }
}
```

Missing audio is a successful item with null URL and expiry and causes no S3 call. Stale audio continues to sign the retained asset. Selected object loss, permission denial, signing failure or timeout fails the request with fixed `503` (`Audio storage is unavailable.`), without silently switching to other audio. Stat/signing occur outside the completed snapshot, honor request cancellation and hold no SQLite write lock. A book disabled or unlinked after that authorization snapshot may still receive that request's link; subsequent requests cannot. Disabling a book does not revoke already issued links, which remain subject to S3 expiry and availability. Re-enabling permits new requests.

Fetch a link when playback is needed, and fetch again after expiry. Responses use `Cache-Control: no-store`, and `expiresAt` is UTC. Do not log, persist or send URLs to analytics. For example:

```javascript
async function playVocabulary(wordId, bookId, meaningId) {
  const query = new URLSearchParams({ bookId, locale: 'en-GB' });
  if (meaningId) query.set('meaningId', meaningId);
  const response = await fetch(`/api/vocabulary/${encodeURIComponent(wordId)}/audio?${query}`, { cache: 'no-store' });
  if (!response.ok) throw new Error(`Audio request failed: ${response.status}`);
  const item = (await response.json()).data.items[0];
  if (item.status === 'missing') return; // Show that audio is unavailable.
  // A stale item remains playable; show that its content needs an update.
  const player = new Audio(item.url);
  await player.play();
  return player;
}
```

Callers handle 400/404/422/429/503, expired links and normal media failures, and release the player when leaving the word. MP3 is the baseline; no browser speech fallback, download proxy, permanent public link or learning UI is supplied. Use the configured public S3 endpoint; any path-prefix proxy must strip the prefix and preserve the signed Host. Ordinary media playback and cross-origin fetch/WebAudio have different CORS needs: configure the bucket/proxy for actual client origins when needed rather than enabling global CORS in Lexarbor. No claim is made about all older browsers.

### Internal playback configuration lifecycle

Playback holds one runtime configuration for its full authorization, selection,
Stat and signing sequence; a public request asking for both accents uses the same
configuration for both. Administrator enablement works after a disabled startup, and
closing audio returns the existing safe 503 for new playback. Requests already
holding a configuration finish on that configuration, including its TTL and public
path prefix. A cancelled or timed-out provider that has not actually ended retains
its client until completion. Credential/PublicEndpoint rotation does not immediately
revoke issued URLs; remote credential validity and URL expiry still apply. Advanced
TTL and timeout settings remain restart-only. The administrator S3 settings API
commits and publishes complete candidates
without restart; its schema, credential operations and save errors are documented
in [System settings](SystemSettings.md#audio-storage-s3).

## Pronunciation preparation

The internal `IPronunciationPreparationClient` prepares one explicit target and
`en-GB` or `en-US` accent per call. It does not expose a new HTTP route, write the
catalog or queue, call TTS, or start automatic generation. Callers supply the current
`EnrichmentInputVersion` from the task repository, complete meaning/position context
and the explicit target. A mismatching source revision is rejected before dispatch.
An empty meaning-specific phonetic returns `InheritsDefault` without a supplier call
or plan; the caller uses the default target and its own version instead. It never
creates a specific phonetic or checks all meanings for equivalent pronunciation.

Task executors supply the `beforeDispatch` callback to check the captured input/text
configuration versions and persist the request record immediately before dispatch.
Local waiting, inheritance and invalid inputs never invoke it. Returning false yields
`Superseded`, no plan and no request. The callback must finish its transaction before
returning; network IO never runs inside it. Persistence failures propagate to the caller.

A ready result carries bounded `EnrichmentPlan` content, its original input/version,
the captured opaque text-configuration version, classification basis and mandatory
human IPA constraint. It also carries `Control` and optional `SpeechPhonetic` for speech
adapters. `Instructions` mode retains free-form pronunciation directions. In `Ipa` mode,
the model must provide one complete spoken-target IPA and the hint must equal that IPA
exactly, with no additional directions; nonempty human IPA must be copied exactly.
Invalid modes, conflicting IPA and extra directions reject the plan. Speech IPA does
not grant permission to write a catalog field: audio-only and phrase plans can carry
it with no writable IPA/POS candidates. An omitted mode retains `Instructions` for
older text integrations. Executors must preserve the whole prepared envelope, including
these controls, input/text versions and classification, when implementing recovery;
persisting only `EnrichmentPlan` cannot reconstruct that envelope. This client itself
persists nothing. A speech adapter must consume the prepared
text and constraints and stop if it cannot express them. Failed, uncertain, inherited
or waiting results have no usable plan: never substitute the raw headword.

Explicit position kinds take precedence over inference. Any `word` position permits
shared default IPA; exclusively `phrase` positions prohibit it. An unclassified
position keeps the existing shared-field permission, while a meaning with any phrase
position receives no new POS. Inference is plan data, never a new position. Only
missing applicable default IPA/POS can be returned as writable candidates, and only
with `fieldsEnabled`. Existing nonempty human IPA remains an enforced speech constraint;
a contradictory model candidate is rejected. Phrase plans return no writable IPA/POS.
Default-reading uncertainty stops the item instead of choosing a meaning's special
reading. Preparation expands `sb.`/`sth.`, resolves template slash alternatives and
provides pronunciation directions for proper names. Semantic quality is still subject
to the separate real-supplier listening acceptance; fixed HTTP fixtures do not prove it.

Requests and responses are limited to 64 KiB, including streamed responses without a
length header. There are at most 100 meanings/position kinds and POS candidates;
spoken text is at most 4096 characters, human/candidate IPA 512, classification basis
512 and supplier pronunciation hint 1024. The complete stored plan is at most 16384
characters, matching task persistence. The configured text timeout (1–300 seconds)
covers dispatch and body reads; caller cancellation propagates without fallback.
Truncated completions, malformed/duplicate/unknown plan fields, unresolved templates,
oversized data and changed human constraints have no usable plan.

Safe results distinguish disabled/missing configuration (waiting, no request), inherited
default (no request), semantic uncertainty/invalid plan, HTTP 429 or recognized
HTTP-200 business throttling (`error.code=rate_limit_exceeded` or `slow_down`),
timeout/connection/HTTP 408/5xx service faults, and terminal credential/parameter
rejection. Positive `Retry-After` delays are preserved, including values above the
normal throttle interval. The caller owns budgets/backoff; `RequestMade` distinguishes
attempted dispatch from local waiting, and throttling must not consume a failure slot.
The client makes no automatic retries. Unknown business-error formats fail safely.

Catalog/model text is data in separate JSON chat messages. Credentials stay in the
authorization header of the captured server-only snapshot. Redirects and cookies are
disabled; HTTP client loggers and OpenTelemetry instrumentation are suppressed for
preparation. Complete supplier responses, exception details and credentials are never
returned or persisted; failures expose fixed codes. A response reflecting the current
API key is rejected. No model/provider combination is enabled for speech by this client.

Protocol references: [Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create),
[JSON mode limitations](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=chat),
and [error codes](https://developers.openai.com/api/docs/guides/error-codes).

## Speech adapters

The internal `IPronunciationSpeechClient` accepts only a successful pronunciation
preparation result. It checks the captured source revision, required human IPA and
text-configuration version before using the current per-locale TTS snapshot. It
never falls back to a headword or drops an unexpressible constraint. Registration
alone does not generate audio, start a worker, write fields, upload objects or bind
audio. There are no new public routes or configuration keys.

The explicit support matrix, checked against official documentation on 2026-11-02:

| Provider and endpoint | Models | Pronunciation mapping and limits |
|---|---|---|
| `MiniMaxNative`, `https://api.minimax.io/v1` or `https://api.minimax.cn/v1` | `speech-2.8-hd` (default), `speech-2.8-turbo` | `POST <endpoint>/t2a_v2`, non-streaming hex MP3. Requires `Ipa` control; one `pronunciation_dict.tone` rule maps the complete `spokenText` to `(SpeechPhonetic)`. The hint contains exactly the same IPA, so no free-form direction is discarded. |
| `OpenAICompatible`, exactly `https://api.openai.com/v1` | `gpt-4o-mini-tts`, `gpt-4o-mini-tts-2025-12-15` | `POST /v1/audio/speech`, binary MP3. `input` contains only prepared speech; `instructions` separately carries UK/US accent, required IPA, speech IPA and pronunciation hints. |
| Same official OpenAI endpoint | `tts-1`, `tts-1-hd` | Plain text only. Any required IPA or pronunciation hint is unsupported and stops before HTTP. Current preparation normally supplies a hint, so these models generally cannot consume its plans. |
| Other models, providers or endpoints | None implied | Safe rejection, even when a third-party model has an identical name. Compatibility of the route does not establish pronunciation capability. |

MiniMax keeps the original prepared text and uses the official dictionary replacement
syntax instead of reading pronunciation directions as text. One surrounding `/.../`
or `[...]` IPA wrapper is removed for the native mapping. Native syntax delimiters,
interjection tags, invalid IPA syntax, absent speech IPA and free-form instruction
mode are rejected. When human IPA exists and preparation changes the original target
text, the adapter cannot safely assign that human IPA to a different spoken target
and stops. Without human IPA, a prepared template or phrase can use a complete
spoken-target IPA. It does not infer token offsets, split IPA or add a reading.

Each locale must explicitly configure both voice and speed. MiniMax accepts 0.5–2;
OpenAI accepts 0.25–4. These are narrower than the settings API's storage bound. The
chosen voice must be available from the provider and appropriate for that accent;
the adapter does not select a default voice, clone a voice or promise a voice's
listening quality. Unknown/unsupported capability yields `capability_unsupported`
and zero HTTP requests. Missing configuration or disabled audio yields waiting.

Executors use `beforeDispatch` to validate the input, text and locale-specific TTS
versions and persist `BeginCall`. The callback ends its transaction before network
IO; false returns `Superseded` without dispatch, and persistence failures propagate.
In-flight calls retain their captured settings. Final field/binding authorization,
call persistence, budgets and scheduling belong to the executor and scheduler.

HTTP 429 and recognized HTTP-200 business throttling return `Throttled` with positive
`Retry-After` delta/date preserved, without sleeping or retrying. MiniMax checks
`base_resp.status_code` before accepting audio: 1002/1039/concurrency or growth limits
are throttling; authentication, balance, parameters and unsupported models stop hot
retrying. Timeout/connection/HTTP 408/5xx and recognized temporary native failures
return `ServiceFailure`; malformed responses/audio return `InvalidAudio`. Unknown
business errors fail safely with fixed reasons. `RequestMade` distinguishes local
waiting/rejection from attempted dispatch. Throttling must not consume the caller's
failure budget, and service recovery must not reset exhausted tasks.

Responses are bounded while reading, including unknown-length streams: binary audio
at most 5 MiB; native JSON at most 10 MiB + 64 KiB; decoded native audio at most 5 MiB.
Automatic results must be at least 300 bytes and pass the shared validator for at
least two complete MPEG Layer III frames. The manual upload contract still accepts
valid two-frame MP3 below 300 bytes. Headers, MIME, HTTP 200 and filenames are not
audio validation. The configured TTS timeout covers headers and body; caller cancellation
propagates. There is no URL download, transcoding, automatic retry or new storage path.
Redirects, cookies, HTTP loggers and OpenTelemetry instrumentation are disabled for
these calls. Only fixed safe error codes leave the adapters, never supplier bodies,
trace IDs, exception messages or credentials.

These checks prove protocol handling, not pronunciation or accent quality. Separate
real listening acceptance remains required for MiniMax and one explicitly supported
OpenAI-compatible combination, covering UK/US, polyphonic words, templates and proper
names. OpenAI currently announces Speech API model shutdown on 2027-01-06; replacing
the model name with a Realtime model is not supported here. This adapter does not
implement a Realtime migration or silently change the MiniMax default.

Official references: [MiniMax synchronous speech and pronunciation dictionary](https://platform.minimax.io/docs/api-reference/speech-t2a-http),
[MiniMax China endpoint](https://platform.minimax.cn/docs/api-reference/speech-t2a-http),
[MiniMax error codes](https://platform.minimax.io/docs/api-reference/errorcode),
[MiniMax rate limits](https://platform.minimax.io/docs/guides/rate-limits),
[OpenAI Create speech](https://developers.openai.com/api/reference/resources/audio/subresources/speech/methods/create)
and [OpenAI text-to-speech guidance](https://developers.openai.com/api/docs/guides/text-to-speech).
