# Administrator meaning phonetics

The administrator detail, whole-book content and unit-content reads include each
meaning's `phoneticUk`/`phoneticUs` (specific values), `pronunciation.uk/us`
(`phonetic` and `source`, either `default` or `meaning`), and `expectedUk`/`expectedUs`.
The word's existing phonetic fields remain defaults. Empty specific values inherit
the default; they are not missing fields to fill automatically. Legacy specific
audio without a specific phonetic remains stored but is not selected.

Each condition is `{ "epoch": 0, "sourceRevision": "<64 lowercase hex characters>",
"ownAssetId": null }`. These are nonsecret concurrency conditions for that exact
meaning and accent, read with the fields in the same SQLite snapshot. The asset
identifies the **own** binding, including a retained legacy binding, never a
default playback fallback. Conditions are not configuration versions or settings
ETags. No supplier settings, credentials, object locations or signed URLs are
included.

## Save text and optional specific phonetics

`PUT /admin/vocabulary-books/{bookId}/words/{wordId}/meanings/{meaningId}` retains
the three required text fields `partOfSpeech`, `meaning` and `example`. The new
phonetic fields are optional: omission preserves the current specific value;
`null`, empty or non-control whitespace clears it; a string sets it. Each provided phonetic
requires its corresponding complete `expectedUk` or `expectedUs` from the read.
Do not send a condition without a value. For example, change UK and preserve US:

```json
{
  "partOfSpeech": "n.",
  "meaning": "a definition",
  "example": null,
  "phoneticUk": "/rɛd/",
  "expectedUk": {
    "epoch": 2,
    "sourceRevision": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    "ownAssetId": null
  }
}
```

Unknown fields (including nested condition fields), missing condition members,
invalid types, negative epochs, invalid revisions, and phonetics longer than 512
characters or containing control characters return 400. Values are trimmed;
equivalence uses the existing Unicode/IPA representation normalization, not an
AI judgment. Equivalent values preserve their stored representation and do not
advance the phonetic epoch, unbind audio or open another generation round.
A meaning with any phrase position rejects a new nonempty specific value, while
an unchanged existing value or clear is allowed; the existing POS rule remains.

The actual serialized writer transaction checks each provided condition, applies
the shared specific-phonetic edit boundary, and replaces the three text fields.
Both accents and all text changes commit together. A stale epoch/revision/own
asset, including A→B→A or a concurrent manual upload, returns 409 with everything
rolled back. Missing resources return 404; wrong book/word ownership and an
equivalent different meaning return 409. IDs are never merged or moved. Existing
administrator Cookie+CSRF, administrator Bearer priority and custom-role policies
are unchanged; ordinary and submission-service credentials cannot edit meanings.
Success retains `{ "success": true, "data": { "success": true } }`.

A real specific change atomically unbinds only that meaning+accent's old audio,
records exact safe deletion authority, invalidates old execution credentials,
and persists new Prepare/Audio work. Nonempty specifics require their own binding
for generation completion even when same-phonetic default playback is available.
Clearing restores default selection and cancels specific work. Default audio,
other accents and other meanings are not removed. Shared objects remain protected.
The task may wait for enabled generation, configuration or storage; no second
manual delete or retry is required. Cleanup has independent continuing retries
and may wait while new generation succeeds. See [enrichment status](Enrichment.md)
and [audio selection](Audio.md#meaning-specific-pronunciation-selection).

The request performs no supplier or object-store IO. Pending work and cleanup
survive restart in the existing database. Text changes also change source input;
the existing scheduler refreshes pending work from the final committed input.
Cancellation/failure before commit rolls back all stages. A lost response can
still represent a committed save: stop the old media, read current detail and
audio metadata, and ask the administrator to compare; never automatically replay
the PUT. Existing links or downloaded audio cannot be revoked immediately.

Old three-field clients preserve both specific values without supplying new
conditions. Import/public DTOs gain no write capability. No migration or new
worker is introduced; existing same-volume backup and schema-version gates apply.
