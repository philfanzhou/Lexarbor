# Textbook audio track catalog

A textbook recording belongs to an existing vocabulary book and one of its units.
A unit can contain several ordered tracks, including when its book is disabled or
has no words or meanings. This catalog is independent of word/meaning pronunciation
bindings and their accents and `sourceRevision`.

Administrators can manage catalog metadata and conditionally upload or replace MP3
content. Metadata management works with `Audio:Enabled=false` and without S3; it
makes no object-storage calls. Administrator preview, public playback and a
management webpage are separate features.

## Authentication and responses

Every route requires the configured administrator role (default `admin`). A Bearer
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
revisions. No DTO exposes asset IDs, object keys, buckets, signed URLs or credentials.
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
