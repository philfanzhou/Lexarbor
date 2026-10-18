# Textbook audio track catalog

A textbook recording belongs to an existing vocabulary book and one of its units.
A unit can contain several ordered tracks, including when its book is disabled or
has no words or meanings. This catalog is independent of word/meaning pronunciation
bindings and their accents and `sourceRevision`.

Only catalog metadata management is available here. Textbook file upload,
replacement, administrator preview, public playback and a management webpage are
separate features. These endpoints work with `Audio:Enabled=false` and without S3
configuration; they make no object-storage calls.

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

## Internal conditional upload persistence

`ITextbookAudioUploadRepository` provides an internal, network-free boundary for
future content upload. It adds no HTTP route or configuration. Callers must perform
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
