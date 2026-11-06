# Administrator enrichment observation and retry

These endpoints require the existing administrator policy, including a configured
custom administrator role. Browser requests use the administrator session cookie;
POST also requires the existing CSRF pair. An explicit Bearer header takes priority
over a cookie and requires an administrator token. Ordinary catalog tokens and
meaning-submission service tokens cannot use these endpoints. No public task API,
batch-start operation, or supplier call is added.

The administrator SPA at `/#/enrichment` consumes this contract. It links to the
existing settings and conditional phonetic editor; browser-side waiting, retry
resolution and cancellation are documented in [the frontend guide](../frontend/README.md#autonomous-enrichment).

All successful responses use `{ "success": true, "data": ... }`. Errors use
`{ "success": false, "message": "..." }` with English messages.

## Read endpoints

- `GET /admin/enrichment/tasks`: persisted steps, including preparation and old
  identities whose current target no longer applies.
- `GET /admin/enrichment/tasks/{id}`: current task plus independently paged
  `rounds`, `calls`, `waits`, and `cleanup` histories.
- `GET /admin/enrichment/summary`: current applicable field/audio/expansion requirements,
  separate persisted-task count, and current text/TTS service health.

List and summary accept exact `wordId`, `meaningId`, `locale` (`en-GB`/`en-US`),
`step` (`Prepare`/`Fields`/`Audio`/`Expand`), and projected `state` filters. State values are
`Pending`, `Running`, `Waiting`, `Succeeded`, `Failed`, and `Cancelled`. List paging
uses `page` (default 1, maximum 1,000,000) and `size` (default 25, maximum 100).
Optional `search` matches a literal substring of the task word or its source/specific
meaning (ASCII case-insensitive; trimmed, at most 200 characters, no control
characters). `%` and `_` are literal characters. Default-field/audio tasks have
no source meaning, so only their word is searched. `attentionOnly=true` restricts
to applicable, still-missing targets with a failed round, missing configuration,
or disabled storage. Normal throttling, service recovery and scheduled retries
remain automatic and are excluded. Both filters also apply to summary demands,
including demands not yet queued.

`sort` accepts `word` (default; preserves word ID ordering), `attention` (failed,
configuration/storage waits, running, pending/other waits, then remaining states),
or `recent` (latest round update first). Ties use word text, word ID, meaning ID,
locale, step, then target ID for stable paging. Detail uses
`historyPage`/`historySize` with the same bounds/defaults; newest rounds/events
come first, with an ID tie-breaker. Each page has `items`, `totalCount`, `page`,
and `size`. Invalid filters/paging return 400; an unknown task returns 404.
Summary adds `activities`: one item each for `Fields`, `Expand`, and `Audio`,
with `step`, `total`, `completed`, `actionRequired`, `unqueued`, and
`missingRequirements`. Counts represent targets, not unique words. `actionRequired`
uses the same predicate as `attentionOnly`, including unqueued demands. Requirements
are safe identifiers: `text`, `supplement`, `ukSpeech`, `usSpeech`, `storage`.
They describe configuration readiness even before discovery; the supplement must
exist and be enabled. Speech requirements are shown after text preparation is
configured and respect the requested locale. Service rejection/recovery is
reported separately in `services`. No model name, credential or storage destination
is exposed. The SPA requests summary with only word/meaning/search/locale so task
state/type filters do not hide progress or setup problems in the current scope.

Summary filters apply to current requirements; `Prepare` is an internal prerequisite
and has no separate current field/audio requirement count.

A response captures configuration once and reads all database totals, page rows,
current states, histories, and cleanup associations in one deferred SQLite snapshot.
GET performs no discovery, queue creation, claim, service probe, or counter update.
Separate requests can observe later commits; pages are not a cross-request cursor.

`Expand` is one source-meaning task with `locale=null`; it is not an accent-specific
pronunciation task. Expansion requirements exclude explicit phrase positions and
words created by expansion. A successful empty result is complete. Source edits
are detected by durable discovery and create a new round before regeneration.
Use `step=Audio` when counting only a meaning's specific-audio requirements.
Expansion shares text service health and the existing per-step retry API; missing
or disabled `MeaningSubmissions:BookId` waits without model calls. See
[Vocabulary expansion](VocabularyExpansion.md) for publication and provenance.

Task rows contain `id`, `wordId`, `word`, `meaningId`, `meaning`, `locale`, `step`,
`applicable`, `missing`, `inheritsDefault`, projected `state`/`waitReason`, `current`
round, and `cleanup`. Current and historical rounds expose only `number`, `state`,
`requestCount`, `consumedFailures`, `failureBudget`, `waitReason`, `errorCode`,
`nextAttemptAtUnixMs`, `createdAtUnixMs`, and `updatedAtUnixMs`.
All timestamps are UTC Unix milliseconds. `current.state` preserves the persisted
round state, while task `state` describes current applicability/readiness. A
previously successful task can therefore show pending work after an input change;
a cleared specific target shows `applicable=false`, `inheritsDefault=true`, and
`Cancelled` without rewriting its old success/failure history.

Summary has `applicableTargets`, `completedTargets`, `missingTargets`,
`unqueuedTargets`, `persistedTasks`, grouped `targets` (`state`, `waitReason`,
`count`), and `services`. Requirements include each applicable default field side,
each default audio side, and each nonempty specific phonetic's own audio side.
Explicit phrase-only entries have no field requirements. Empty specifics inherit
and do not add missing targets; legacy bindings do not turn inheritance into
specific success. Same-phonetic default playback fallback does not satisfy a
missing specific own binding. An unconfigured deployment remains missing
and waiting (`ConfigurationMissing`), never completed. Counts of preparation tasks and
requirements have different denominators and must not be added together.

Wait reasons include `Disabled`, `ConfigurationMissing`, `StorageDisabled`,
`ServicePaused`, `Throttled`, `Retry`, and `LeaseExpired`. Each service exposes only
`service` (`text`/`tts`), `paused`, `blocked`, `consecutiveFailures`, `throttleSeconds`,
and `nextAttemptAtUnixMs`. Requests and consumed failure allowance are independent:
seven throttled requests may still have zero consumed failures. A failed exhausted
round stays `Failed` when its service recovers. Safe errors distinguish rejected
provider configuration/capabilities, preparation/field failures, and generic
failures; arbitrary stored error strings are replaced with fixed safe summaries.
Preparation errors preserve `text.phonetic_constraint_mismatch` (reading violates
the catalog constraint), `text.template_unexpanded` (unresolved speech template or
native text syntax), and `text.pronunciation_control_unsupported` (the captured
adapter cannot express the plan). They fail only that target; they neither expose
model responses nor pause the shared service as an outage.

`cleanup` gives independent `waiting`, `running`, `observed`, `attempts`,
`nextAttemptAtUnixMs`, and safe `errorCode`. Detail cleanup history contains the
explicit authorization `epoch`, state, attempts, retry time, safe error, and
creation/update times. It includes prior invalidation epochs for that exact
word+meaning+locale, not another accent. The old binding was already removed
before this work was queued. Cleanup failure can coexist with successful new
audio; clearing the specific phonetic restores inheritance even while deletion
waits. Cleanup has its own continuing retry policy, not the generation allowance.

Plans, provider responses, configuration versions/fingerprints, credentials,
execution leases, upload/retry keys, asset/object locations, buckets, and signed
URLs are never returned. Calls contain only an event ID, round, service, result,
safe error, and start/finish times. Waits contain round, reason, safe error, and
recorded/next times. Render catalog text as text, not HTML.

## Retry one failed step

`POST /admin/enrichment/tasks/{id}/retry` accepts only:

```json
{
  "expectedRound": 1,
  "retryKey": "75df64ad-f046-4e49-b558-2dcf4fa61dc5"
}
```

`expectedRound` must be the failed current round. `retryKey` is a fresh canonical
lowercase UUID for this one user action. Requests are limited to 1 KiB; unknown,
duplicate, or invalid fields return 400 (oversized bodies return 413).

The same serialized SQLite writer checks current round, failed state, target
applicability and remaining missing work, then creates the new round. Unknown ID
returns 404; a stale round, nonfailed/completed/cleared target, or different-key
concurrent loser returns 409. Do not pre-read and then issue an unconditional
retry. Only one request for a given expected round can create its successor.
A same-key repeat returns the originally accepted round across restarts and later
rounds. Successful data and the other accent are not redone. Success returns:

```json
{ "success": true, "data": { "targetId": "task-id", "round": 2, "replayed": false } }
```

A repeated accepted request has `replayed=true`. Retain that action's key only
while resolving an unknown outcome; reload state before offering a new action.
Never silently repeat a request with a newly generated key after a timeout.

Retrying `Fields` also invalidates its corresponding `Prepare` in the same
transaction, including a successful plan that omitted candidates or encountered
equivalent meanings. Retrying `Prepare` releases only still-missing failed
`preparation.failed` dependencies for the same target/accent. Completed steps and
other failures remain intact. Any failure/cancellation before commit rolls back
all changed rounds and epochs. Post-commit wakeup is best effort; durable scans
recover it. A successful preparation may write missing fields and cause the
existing input-version rules to advance a dependent field round again. The retry
receipt identifies the round created by the action, not a promise that it remains
current forever. Configuration correction and ordinary discovery still work
without this endpoint; retry never enables generation or starts a manual batch.

Retrying a legacy `Audio` failure with `tts.capability_unsupported` or
`tts.prepared_plan_required` also refreshes its preparation in that transaction;
network/audio-publication failures retain successful preparation. Old failed
preparations may require one explicit retry after upgrading. The upgrade does not
bulk-reset exhausted work or regenerate completed audio.

### Text request counters

The summary optionally includes `textRequests` with nonnegative integer fields
`requests`, `participations`, `completed`, and `unknown`. These are global counters
since batch scheduling was introduced, independent of task filters. They include
preparation, vocabulary expansion and automatic submission review, including
single-item fallback requests; they exclude TTS and older call history.
`requests` counts persisted dispatch attempts, `participations` counts member
attempts, `completed` counts validated/staged member results, and `unknown` counts
requests still running or lacking a confirmed outcome. These counters do not
represent unique words, published items, provider billing, or token savings.
Older clients may ignore this additive field; the admin client tolerates its
absence when connected to an older server.
