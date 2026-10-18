# Textbook audio track catalog

A textbook recording belongs to an existing vocabulary book and one of its units.
A unit can contain several ordered tracks, including when its book is disabled or
has no words or meanings. This catalog is independent of word/meaning pronunciation
bindings and their accents and `sourceRevision`.

Administrators can manage catalog metadata and conditionally upload or replace MP3
content. Metadata management works with `Audio:Enabled=false` and without S3; it
makes no object-storage calls. Administrators can request short-lived preview links
for uploaded tracks. Anonymous clients can list available tracks in enabled books and request short-lived playback links. The administration webpage exposes catalog creation, conditional metadata/order/availability editing and confirmed removal; browser content upload and textbook preview are separate follow-up stages.

## Authentication and responses

Every `/admin` route requires the configured administrator role (default `admin`). A Bearer
header takes precedence over a session cookie; rejected Bearer credentials never
fall back to the cookie. Unsafe cookie requests require the normal CSRF token pair.
Anonymous requests return 401, authenticated non-administrators 403. All responses
carry the administration security headers and `no-store` baseline. Errors follow
the existing safe error / Problem Details mapping; storage contention returns 503
with `Retry-After: 1`.

Success is HTTP 200 with `{ "success": true, "data": ... }`. A track DTO contains
exactly these fields:

```json
{
  "id": "server-generated-id",
  "bookId": "book-id",
  "unitId": "unit-id",
  "title": "Listening exercise",
  "displayOrder": 0,
  "hasFile": false,
  "durationSeconds": null,
  "isAvailable": false,
  "version": "00000000-0000-0000-0000-000000000001"
}
```

IDs in paths are opaque nonempty strings, trimmed before lookup; existing book and
unit IDs need not be UUIDs. `version` is a server-generated canonical lowercase UUID
D string, used only for exact comparison. It is separate from pronunciation source
revisions. Catalog DTOs expose no asset IDs, object keys, buckets, signed URLs or credentials.
`hasFile` describes a current reference, not successful object storage access.

## List tracks

`GET /admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks?page=1&size=20`

Returns `data: { "items": [...], "totalCount": 3, "totalPage": 1 }` in
`displayOrder ASC, id ASC` order. Repeated display orders are allowed. Omitted or
zero `page`/`size` default to 1/20; size is capped at 100. Negative, overflowing,
unparseable or repeated pagination values return 400. A valid empty unit returns
`items: []`, `totalCount: 0`, `totalPage: 0`. Ownership, count and page are read from
one SQLite snapshot without taking the global writer lock.
An external SQLite lock can prevent opening or reading this snapshot. Such busy
or locked failures return the same safe 503 with `Retry-After: 1` as catalog writes;
the list request does not modify stored data and can be retried after the lock clears.

## Create a track

`POST /admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks`

```json
{ "title": " Listening exercise ", "displayOrder": 0 }
```

Both fields are required. The title is trimmed, must remain nonempty, and must fit
200 .NET UTF-16 code units (a supplementary character counts as two). Display order
is an integer from 0 to 2147483647. The server assigns ID/version; the track starts
without a file, with null duration and unavailable. POST has no idempotency key:
if its response is lost, read the list to check the outcome before creating again.
A disconnected request is not proof that creation was rolled back.

## Replace metadata and availability

`PUT /admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks/{trackId}`

```json
{
  "title": "Listening exercise",
  "displayOrder": 1,
  "isAvailable": false,
  "expectedVersion": "00000000-0000-0000-0000-000000000001"
}
```

All four fields are required. Success returns the updated DTO and a new version,
including for a same-value PUT. Ownership, current version and file state are read
after entering the same short write transaction. Current file and duration are
preserved. Enabling requires a current file with a positive finite duration and
an asset with a recorded first publication time; otherwise it returns 409 without
writing. Availability is an explicit administrator decision, not a promise that an
object is currently readable. Catalog metadata cannot publish a file.

## Remove a track

`DELETE /admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks/{trackId}?expectedVersion=...`

One canonical `expectedVersion` query value is required. Success returns
`{ "success": true, "data": { "success": true } }`. Only the catalog row is
removed. Assets, historical operation receipts and physical objects are retained.
Removal is not permission to physically clean an asset.

## Refusals, concurrency and cancellation

Null bodies, missing/wrongly typed required fields, unknown fields, invalid
titles/orders and invalid versions return 400. File references, durations,
ownership, IDs or new versions supplied as metadata are rejected rather than
ignored. Missing books/units/tracks return 404. An existing unit under another
book, or track under another book/unit, returns 409. Stale expected versions return
409; read the current row before deciding whether to retry. Two writes with the
same version can succeed at most once, including PUT/DELETE races. The service
reads current database values even if a context tracked an older entity.

Ordinary book deletion, unit deletion and whole-book cleanup `action=delete`
return a clear 409 when tracks remain. Cleanup delete preview also returns 409.
Checks and deletes share a transaction, with RESTRICT foreign keys as a backstop;
cleanup cannot delete vocabulary and then leave a partially removed book. Other
cleanup actions (`clear`, `removeWords`, `removeMeaning`) preserve tracks and their
file references. Empty-catalog parent deletion keeps its existing behavior.

Cancellation before write admission or before commit rolls back the transaction;
write/commit failures leave no partial catalog change. Once commit starts, its
outcome can be unknown to a disconnected caller; read again to establish it.
Creation racing with parent deletion either succeeds before a protected 409 delete,
or sees the deleted parent as 404. No orphan track survives.

See [database storage](../database/README.md#textbook-audio-catalog) and
[upgrade and backup](../development/Deployment.md#textbook-audio-catalog-upgrade).

## Upload policy

`GET /admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks/upload-policy`

After administrator authorization, this checks the book and unit ownership in one
read snapshot. It does not look up a track or contact S3. It remains readable while
audio is disabled. Success contains exactly:

```json
{"success":true,"data":{"enabled":false,"maxUploadBytes":67108864,"maxDurationSeconds":7200}}
```

Limits are the bounded effective configuration captured once for this request.
They are server configuration, separate from the online S3 settings JSON. Missing
book/unit returns the existing 404 Problem Details; a unit belonging to another
book returns the existing 409 Problem Details. Busy/locked SQLite reads return the
existing 503 Problem Details with `Retry-After: 1`.

## Conditionally upload or replace content

`PUT /admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks/{trackId}/content`

Send one canonical lowercase UUID D `Idempotency-Key` header and exactly two
`multipart/form-data` sections:

- `expectedVersion`: the current canonical lowercase UUID D version, exactly 36 UTF-8 bytes.
- `file`: one complete MP3 file; its filename and MIME type are hints and do not establish validity.

The fields may appear in either order. Unknown/duplicate fields, extra files,
client durations, buckets, keys and URLs are refused. Cookie authentication needs
the normal CSRF token pair in the cookie and `X-SignaCore-CSRF` header. A missing
header is rejected without reading this route's multipart body; Bearer precedence
is unchanged. Authorization and CSRF happen before upload admission or media parsing.

A boundary has at most 128 characters; each section has at most eight headers and
8192 header bytes. Total multipart overhead, including preamble and epilogue, is
at most 64 KiB. Both declared lengths and streams without `Content-Length` are
bounded. New files use `Audio:MaxTextbookUploadBytes` (default 64 MiB, range 1–128
MiB); PCM duration uses `Audio:MaxTextbookDurationSeconds` (default 7200 seconds,
range 1–14400). The word pronunciation limit is unchanged. Both upload types share
one per-host admission queue, including body reads, validation and transfer. Queue
capacity/wait refusal is 429 with `Retry-After: 1`; read/validation/transfer use the
captured `Audio:OperationTimeoutSeconds` deadline. Media decoding is synchronous,
checks cancellation/deadline between frames and during bit reads, and never leaves
a background decoder running after releasing admission. A timed-out or disconnected
request stops waiting promptly, but a storage Put that ignores cancellation keeps
the same shared admission slot, client and input stream until it actually finishes.
Until then, both upload types can receive 429 before body reading or reservation.
Late storage success cannot publish the timed-out or cancelled upload.

The textbook byte limit applies independently through both HTTP validation and the
object store Put, using the same captured runtime configuration. A valid textbook
file may exceed the vocabulary `Audio:MaxUploadBytes` limit. Each upload type must
still fit its own limit, even when the other type permits larger files. The upload
purpose is selected by the server and is never a multipart field or inferred from
an object key.

The server checks complete Layer III headers, lengths, consistent MPEG version,
sample rate and channel mode, optional CRC-16 and sequential bit-reservoir decoding
with NLayer 3.0.0. Each frame must produce all expected finite PCM samples. Free
bitrate, resynchronization, incomplete output and corrupt/truncated frames are
refused. ID3v2.2–2.4 tags are skipped within a 1 MiB bound; a v2.4 footer must match.
An optional final 128-byte ID3v1 tag is permitted. The decoder uses a fixed 2304-float
output buffer; frame/sample counts and input bit reads are bounded by source length
and configured duration. There is no transcode or external decoder process.

`durationSeconds` is the complete decoded PCM sample count divided by sample rate
and channel count, including encoder delay and padding. Xing/VBRI frame counts,
ID3 duration fields and LAME gapless trimming never shorten validation or duration.
This measurement may be slightly longer than a player's gapless display. Validation
does not promise detection of every still-decodable unprotected bit change, audio
quality, or support on every player.

Success is HTTP 200 and contains exactly:

```json
{"success":true,"data":{"trackId":"track-id","version":"00000000-0000-0000-0000-000000000002","durationSeconds":0.5746938775510204}}
```

Validation and hashing finish before a new reservation. The server persists its
operation and random immutable object location, begins transfer, writes S3 outside
SQLite transactions, then conditionally publishes the file, measured duration and
new version together with the permanent receipt. Publication always sets
`isAvailable=false`; title and order are preserved. Explicitly enable the recording
through metadata after checking it. A stale version or edits/deletion during Put
cannot replace the prior file. Published historical objects are retained.

Keep the original key, target, expected version and original bytes if the response
is lost. A known key is matched against its original size (with an absolute 128 MiB
cap), SHA256 and versioned request fingerprint. A matching terminal operation
replays its original success/failure receipt, even after replacement, catalog
removal, restart or tighter new-upload limits; it is not decoded again or
republished. A different request or matching active operation returns 409. Audio
being disabled still rejects this request with 503. Reservation rechecks the key
atomically to cover concurrent first requests. After 409, re-read the target; do
not blindly generate another key when a prior result is unknown. Failed or unknown
publication keeps its durable operation for bounded recovery.

Correcting storage configuration or a storage implementation does not change an
existing terminal receipt. This includes a 503 stored by an earlier upload: an
exact retry keeps that 503 without another Put or publication. After confirming a
terminal failure, a new logical upload uses a new idempotency key and the current
expected version; an unknown result still requires checking the original key.

Upload-local errors use JSON, for example:

```json
{"success":false,"message":"Textbook audio file must contain complete decodable MP3 frames."}
```

| Status | Fixed upload-local message or boundary |
| --- | --- |
| 400 | `Textbook audio multipart body is invalid.`; invalid/missing/noncanonical idempotency header: `Idempotency-Key must be a canonical lowercase UUID.` |
| 404 | `Textbook audio target was not found.` |
| 409 | `Textbook audio upload conflicts with the target or idempotency key.` |
| 413 | `Textbook audio file is too large.` (file or overhead) |
| 415 | `Textbook audio file must contain complete decodable MP3 frames.` |
| 422 | `Textbook audio duration exceeds the configured limit.` |
| 429 | `Audio upload capacity is exhausted. Retry later.` with `Retry-After: 1` |
| 503 | `Audio storage is disabled.` or `Audio storage is unavailable.` |

Authentication keeps 401/403, and blank path IDs/database contention retain the
existing Problem Details mapping, including 503 plus `Retry-After: 1` for SQLite
busy/locked. Every success or failure is `no-store` with the administrator security
headers. Decoder/provider exceptions, internal locations, credentials and signed
URLs are not included in upload responses or logs. Cancellation before reservation
creates no operation; after reservation the operation keeps its original failure
receipt or an expiring lease. An unknown commit may have succeeded: replay the
original request to establish its result.

## Administrator playback

`GET /admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks/{trackId}/playback`

This read requires the configured administrator role; Cookie GET needs no CSRF
pair and Bearer credentials retain priority. It permits disabled books, empty books
and tracks with `isAvailable=false`. Those administrator permissions do not grant
public playback. Path IDs are trimmed opaque nonempty strings; whitespace is 400.

Success is HTTP 200 with exactly these three `data` fields:

```json
{"success":true,"data":{"trackId":"track-id","url":"https://audio.example.test/short-lived-signed-link","expiresAt":"2026-10-18T04:00:00+00:00"}}
```

One deferred SQLite read snapshot checks book, unit ownership, track ownership and
the current published asset, in that order. Missing book/unit/track or no published
current file returns 404; an existing unit under another book, or track under
another book/unit, returns 409. SQLite busy/locked during connection opening,
transaction setup, reads or commit returns safe 503 with `Retry-After: 1`.
The transaction and connection close before any storage call. The service checks
that captured object once and signs that same object once; a concurrent replacement
or catalog/parent deletion cannot change selection or hold a SQLite transaction
across network IO. There is no fallback to another recording.

Audio must be enabled. Disabled storage returns 503
`{"success":false,"message":"Audio storage is disabled."}`. A missing object,
provider denial/failure or operation deadline returns 503 with the fixed message
`Audio storage is unavailable.`. These failures never return an empty success,
provider detail, object key, bucket or credentials. Caller cancellation stops waiting.
All successful and failed responses retain `no-store` and administrator security
headers, including authorization, routing and exception responses.

Links use the captured `Audio:PlaybackTtlSeconds` and `Audio:OperationTimeoutSeconds`;
`expiresAt` is UTC. An entire request retains one runtime generation, and a provider
call that ignores cancellation keeps its original client alive until the real call
ends. Disabling audio rejects new requests; captured requests follow their original
generation. Fetch a fresh link for each playback, retry after expiry/failure, and
keep it only in the current playback scope. Never log it or persist it in analytics,
local storage or session storage. Removing a track, changing availability or disabling
a book does not immediately revoke an already signed link. This endpoint returns a
link, not proxied audio bytes, and does not introduce student identities.

## Internal conditional upload persistence

`ITextbookAudioUploadRepository` provides an internal, network-free boundary for
conditional content upload. The persistence boundary itself adds no HTTP route or
configuration. Callers must perform
authorization and bounded media validation before reserving, and use only captured
server storage settings and a server-measured positive finite duration. Media and
object-storage IO must run between repository calls, outside any UnitOfWork.

Each canonical lowercase UUID idempotency key is permanently scoped to textbook
uploads, independently of pronunciation uploads. Its lowercase SHA256 fingerprint
is computed over the UTF-8 JSON array
`["textbook-audio-upload",1,bookId,unitId,trackId,expectedVersion,sha256,byteLength]`
in exactly that order. IDs remain trimmed opaque strings. A new reservation checks
book, unit, track ownership and the canonical expected version, then atomically
creates one immutable random object location and operation. Existing keys are
resolved before looking up the current directory: matching active requests report
in-progress, matching terminal requests replay the original receipt, and mismatched
fingerprints conflict. A missing target or stale condition creates no asset.

Publication requires a transferring operation, matching owner/fence and a lease
strictly later than the current time, re-read after writer admission. In one commit
it rechecks ownership/version, writes the current asset and duration, sets
availability false, generates a new version, records first publication and stores
the original track/version/duration receipt. Title and order are preserved.
Metadata changes, another publication or deletion during transfer produce a fixed
409/404 failure receipt without altering the current file. Database or commit
failure rolls back all publication writes; an unknown commit outcome must be
resolved with the original key. Cancellation while queued writes nothing; after
admission the short database transaction finishes independently of cancellation.

Receipts survive later replacement, target/parent deletion and cleanup. Failed
operations retain their known object location. Only never-published failures at
least 24 hours old, with no live upload lease and a due retry, can be conditionally
claimed for cleanup. Cleanup uses its own owner/fence/expiry and keeps the original
receipt. Observed deletion leaves a permanent tombstone for later rechecks; a late
transfer cannot publish. Any asset ever marked published remains protected from
automatic cleanup, even after replacement or removal. The shared recovery worker
now processes textbook operations through these conditional methods; see
[recovery and backups](../development/Deployment.md#failed-upload-recovery).

## Public track list and playback

These anonymous GET routes share the existing `/api/*` per-client rate limit.
They require no student identifier or administrator credentials. An attached ordinary
session or invalid Bearer token does not turn them into administrator routes. They
read books and units directly; neither needs to contain words or meanings. Every
response from these two routes, including errors and rate refusals, has
`Cache-Control: no-store`. Existing pronunciation DTOs and quotas are unchanged.

```http
GET /api/vocabulary-books/book-id/units/unit-id/audio-tracks?page=1&size=20
```

```json
{"success":true,"data":{"items":[{"id":"track-id","bookId":"book-id","unitId":"unit-id","title":"Listening exercise","displayOrder":0,"durationSeconds":0.5746938775510204,"version":"00000000-0000-0000-0000-000000000002"}],"totalCount":1,"totalPage":1}}
```

Each item has exactly those seven fields. Only available tracks whose current asset
has a recorded publication time are included. Order is `displayOrder ASC, id ASC`.
Pagination has the administrator list's defaults and validation: omitted/zero page
and size mean 1/20, size is capped at 100; negative, overflowing, non-decimal and
duplicate values are 400. A large valid page is empty without overflowing the offset.
The filtered total and items share one SQLite snapshot. Empty units return
`{"success":true,"data":{"items":[],"totalCount":0,"totalPage":0}}`.
Listing performs no object-storage calls: metadata does not prove an object is readable.

```http
GET /api/vocabulary-books/book-id/units/unit-id/audio-tracks/track-id/playback
```

```json
{"success":true,"data":{"trackId":"track-id","url":"https://audio.example.test/signed-example","expiresAt":"2026-10-18T04:00:00+00:00"}}
```

The three fields are the entire playback DTO. `expiresAt` is UTC; validity uses
`Audio:PlaybackTtlSeconds`. The illustrative URL above is not a usable signature.
No independent asset ID, bucket, key, settings or credentials are exposed. SQLite
selects the enabled book, unit ownership, available track and published current
asset in one completed read snapshot. Only then does the captured runtime's object
store Stat and sign that same asset. Storage I/O holds no SQLite transaction or
writer lock. Replacement, unavailability, book disabling or deletion after that
snapshot can leave this request signing its originally authorized asset; a new
request reads the new state. Requests and actual provider calls retain their own
runtime generation during rotation, disable/re-enable, cancellation or deadlines.

Validation precedes the runtime enabled check, then book existence/status, unit
ownership and finally public track selection. Refusals are:

| Condition | Status and safe response |
| --- | --- |
| Blank trimmed opaque ID; invalid/repeated list pagination | 400, e.g. `{"success":false,"message":"An ID is required."}` or `{"success":false,"message":"Page and size must be nonnegative integers."}` |
| Audio disabled | 503, `{"success":false,"message":"Audio storage is disabled."}` |
| Missing book, missing/cross-book unit, missing/cross-unit/hidden/fileless/unpublished track | 404 Problem Details, `errorCode: "vocabulary.not_found"` |
| Disabled book | 422 Problem Details, `errorCode: "vocabulary.business_rule"` |
| SQLite busy/locked | 503 Problem Details, `errorCode: "vocabulary.storage_busy"`, `Retry-After: 1` |
| Selected object missing, access denied, Stat/sign error or timeout | 503, `{"success":false,"message":"Audio storage is unavailable."}` |
| Shared public budget exhausted | Existing 429 response and a positive `Retry-After` header |
| Unexpected database failure | Existing safe 500 Problem Details |

For example, an unavailable track returns HTTP 404 and `application/problem+json`:

```json
{"type":"urn:servicemantle:error:vocabulary.not_found","title":"The requested resource was not found.","status":404,"correlationId":"example-correlation-id","errorCode":"vocabulary.not_found"}
```

The correlation ID is request-specific and also appears in `X-Correlation-Id`.
The same track may still be previewed by an administrator. Storage failure never
becomes an empty list, a different recording or a provider exception in the response.

Consumers own their login boundary and should obtain a fresh link for each user
click, handle expiry/renewal and media failures, and ensure the public S3 endpoint
is reachable. Keep signed URLs only in the active playback scope; never log, analyze
or persist them in browser storage or a database. Signed links can remain usable
until S3 expiry after a track is hidden, replaced or removed or its book is disabled.
There is no immediate revocation, cross-request catalog snapshot, byte proxy or
promise of support on every device. Downstream deployments must check their actual
Lexarbor release before using these additive routes. Rolling back these routes
requires no migration and preserves all stored recordings.
