# ADR-005 Bulk vocabulary import

- **Status**: accepted; implemented
- **Date**: 2026-09-25
- **Scope**: The `POST /admin/vocabulary/batch` contract, the TSV format the administration UI accepts, and the limits and failure semantics of one batch. Existing routes, JSON fields, the SQLite schema, and authentication are unchanged. The other formats the UI accepts, and how it reads files, are decided in [ADR-006](./ADR-006-batch-import-file-formats.md)

## Context

Administrators fill a book one entry at a time through `POST /admin/vocabulary`, and each call opens its own transaction. Lexarbor ships no vocabulary data ([ADR-002](./ADR-002-bundled-vocabulary-data.md)), so every book in an instance is typed in by hand. ADR-002 requires a bulk-import format to have its own decision. [Issue #70](https://github.com/philfanzhou/Lexarbor/issues/70) tracks the feature; [Issue #72](https://github.com/philfanzhou/Lexarbor/issues/72) holds the API slice this decision was written for.

Two properties of the storage layer shape the decision. First, SQLite has one writer, and a process-wide lock serializes every write: while a batch is being written, every other administrative write waits, although anonymous reads do not because the database runs in WAL mode. Second, the single-entry path already defines how an entry is matched against stored data. A word is shared across books and matched by `lower(trim(word))`. A meaning belongs to one book and is matched by word, book, normalized part of speech, and trimmed definition. A bulk path that matched differently would give the same data two meanings.

## Decision

### Route and payload

`POST /admin/vocabulary/batch` joins the existing `/admin` group: it requires the `VocabularyAdmin` policy, and a cookie-authenticated request needs the same `X-Requested-With` header as every other administrative write.

```json
{
  "bookId": "book-id",
  "entries": [
    { "word": "apple", "phoneticUk": "/ˈæp.əl/", "phoneticUs": "/ˈæp.əl/", "partOfSpeech": "n.", "meaning": "苹果", "example": "I eat an apple." }
  ]
}
```

- `word` and `meaning` are required and must not be blank after trimming.
- `phoneticUk`, `phoneticUs`, `partOfSpeech`, and `example` are optional. A blank string counts as absent, so an empty column never clears a stored phonetic or example.
- Success answers 200 with `{ "success": true, "data": { "total": 3, "created": 2, "reused": 1 } }`. `created` counts the meanings the batch inserted; `reused` counts the entries that matched an equivalent meaning, whether stored earlier or written by an earlier entry of the same batch; `total = created + reused`. `data` carries exactly these three fields.
- Invalid entries answer 400 with an `errors` array that lists every invalid entry in ascending `index` order, where `index` is the zero-based position in `entries`:

  ```json
  { "success": false, "message": "2 entries are invalid.", "errors": [ { "index": 1, "message": "Meaning is required." }, { "index": 3, "message": "Word is required." } ] }
  ```

  `errors` appears only on this route. Every other route keeps the existing failure envelope.

### Entries apply in order, as the single-entry path would

The entries of one batch are applied in array order with the normalization and matching of `POST /admin/vocabulary`. The result is the same as posting them one by one, except that the batch is atomic. A later entry's non-blank phonetics and example overwrite an earlier one's. Phonetics live on the shared word row, so a batch for one book can change the phonetics another book displays for the same word; that is the existing single-entry behaviour.

### A batch is atomic

Every check that needs no database runs first. The batch is then written in one transaction, and any failure rolls the whole batch back. Checks run in this order, and the first one that fails decides the answer; nothing is written unless the last row is reached:

| # | Condition | Answer |
|---|---|---|
| 1 | Not authenticated | 401, existing envelope |
| 2 | Authenticated without the administrator role | 403, existing envelope |
| 3 | Body larger than 1 MiB, including a chunked body | 413 `The request body is too large.` |
| 4 | Body missing, not declared as JSON, or not valid JSON for this shape | 400 `The request body is not valid JSON.` |
| 5 | `bookId` blank | 400 `Book ID is required.` |
| 6 | `entries` missing or empty | 400 `At least one entry is required.` |
| 7 | More than 500 entries | 400 `A batch can contain at most 500 entries.` |
| 8 | An entry is `null`, or its `word` or `meaning` is blank | 400 with `errors` listing every invalid entry |
| 9 | The book does not exist | 404 `Vocabulary book was not found.` |
| 10 | The book is disabled | 422 `New meanings cannot be added to a disabled vocabulary book.` |
| 11 | All entries valid | 200 with `{ total, created, reused }` |
| 12 | A constraint violation, a busy database, or an unexpected error while writing | 409, 503 with `Retry-After: 1`, or 500 through the existing middleware; the whole batch is rolled back |

Rows 1 to 8 take no write lock. Rows 9 and 10 are checked inside the write transaction, so a concurrent disable or delete cannot land between the check and the writes.

Atomic semantics were chosen because they make resubmission safe. A batch that failed wrote nothing, and a batch that succeeded matches itself on the next attempt, so a corrected batch can be sent again whole without duplicating rows. Nothing is left half-imported, and the result does not depend on the browser remembering which rows already went through. A client that disconnects mid-write gets the same guarantee: the server commits or rolls back the whole batch, and the client can resubmit, which is idempotent.

### Limits

Both limits are code constants and are not configurable.

- **500 entries per batch.** A batch holds the write lock for its whole duration. Measured on a file-backed WAL database with each entry applied through the single-entry logic inside one transaction:

  | Entries | First import | Idempotent resubmission |
  |---|---|---|
  | 200 | 1.8 s (includes JIT warm-up) | 0.3 s |
  | 500 | 3.0 s | 0.7 s |
  | 1000 | 4.7 s | 1.6 s |
  | 2000 | 8.8 s | 4.3 s |

  500 keeps the lock hold in seconds on a modest host. An implementation may get faster without changing semantics, but that is no reason to raise the limit.
- **1 MiB (1,048,576 bytes) request body.** 500 entries of ordinary vocabulary need a fraction of it. The limit is enforced by Kestrel while the body is read, so a chunked body without `Content-Length` is bounded too. The endpoint reads the body itself rather than binding it as a parameter, because with parameter binding Kestrel answers an oversized body with an empty 413 and the shared envelope is lost. The exception middleware maps Kestrel's 413 to the envelope.

A caller with more than 500 entries splits them into several batches. Each batch is atomic, but separate batches are not: earlier batches stay committed if a later one fails.

### Parsing happens in the browser

The server accepts JSON only, never a file, so no upload surface is added. The administration UI parses TSV and submits JSON. The server validates every entry itself and does not rely on validation done in the browser.

The TSV format the UI accepts:

- UTF-8 text, one entry per line, columns separated by a tab;
- columns in order: `word`, `phonetic_uk`, `phonetic_us`, `part_of_speech`, `meaning`, and an optional sixth `example`;
- blank lines and lines starting with `#` are ignored;
- every other line must have exactly five or six columns.

A file must be UTF-8; the page refuses a file that is not, rather than importing replacement characters. The page also accepts CSV, and [ADR-006](./ADR-006-batch-import-file-formats.md) defines the formats other than TSV, how a file is read, and the rules they share.

### Logging

The endpoint logs the book id, the entry count, and the result counts. It never logs the request body or entry content.

### User-supplied data

Imported entries are user-supplied data under [ADR-002](./ADR-002-bundled-vocabulary-data.md). Lexarbor stores them as given and makes no judgement about their source. This feature adds no official dataset, no source-specific downloader or converter, and no suggestion that Lexarbor grants any right to imported content. Users and instance operators are responsible for having the rights to what they import.

## Alternatives considered

- **Commit entry by entry and report per-row results**: rejected. A failure leaves a partial batch behind, and only the client can know which rows to resend.
- **Loop over the single-entry API from the browser**: rejected. It has the same partial-batch problem and costs one HTTP request per row.
- **Upload the TSV file and parse it on the server**: rejected. It adds a file upload surface and a server-side parser for a format the browser can turn into JSON.
- **Configurable limits**: rejected. The entry limit exists to bound write-lock hold time; making it configurable would move that trade-off onto every operator.

## Consequences

- Administrators can import up to 500 entries per request, from the administration UI's `/import/batch` page, which parses the TSV in the browser as described in the [frontend specification](../frontend/README.md#batch-import-page). [ADR-006](./ADR-006-batch-import-file-formats.md) adds CSV to the same page.
- Resubmitting a batch creates no duplicate `vocabulary` or `vocabulary_meaning` rows, and a failed batch leaves no trace.
- While a batch is being written, other administrative writes wait for up to a few seconds; anonymous reads are unaffected.
- The exception middleware now maps Kestrel's body-too-large error to 413 with the envelope. Other routes bind their body as a parameter, so the framework handles an oversized body before the middleware sees it and still answers Kestrel's default 30 MB limit with an empty 413. Their contracts are unchanged.
- The SQLite schema and migrations are unchanged; rows written by a batch have the same shape as rows written one by one, so removing the route loses no data.
