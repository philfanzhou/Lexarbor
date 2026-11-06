# Vocabulary relation management

Relations connect existing global `vocabulary` rows, independently of vocabulary
books. One directed row stores `sourceWordId -> targetWordId / type`. Reading
either endpoint returns the same relation ID; no inverse row is created.
Relations alone do not add meanings, book memberships, pronunciation, or quiz options.
[Automatic expansion](VocabularyExpansion.md) can create new words and supplement
meanings together with generated relations in one transaction.

These are administrator APIs. They use the configured administrator role and the
existing Bearer/session authentication boundary. Session-authenticated writes
require the normal antiforgery header/cookie pair. All successes return the
standard `{"success":true,"data":...}` envelope. Failures follow
[Error handling](../development/ErrorHandling.md). There is no public relation
API or relation administration UI in this release.

## Relation object

| Field | Meaning |
|---|---|
| `id` | Server-generated GUID string identifying the relation |
| `sourceWordId`, `targetWordId` | Existing word IDs; self-relations are allowed |
| `type` | One of the codes below |
| `usageNote` | Nullable, trimmed applicability description, up to 2,000 characters |
| `status` | `needs_review` or `confirmed` |
| `reviewReason` | `new_relation`, `word_changed`, `relation_changed`, or null when confirmed |
| `originKind` | Initial provenance: `manual`, `import`, or `generated` |
| `originReference` | Nullable initial batch/task ID; never supplied by a maintenance client |
| `version` | Positive integer used for conditional maintenance |
| `createdAtUnixMs`, `updatedAtUnixMs` | Server-controlled UTC Unix milliseconds |
| `lastConfirmedAtUnixMs` | Nullable last explicit confirmation time |

The unique key is `(sourceWordId, targetWordId, type)`. There are no source or
target part-of-speech fields. Different grammatical uses of the same pair and
type share one row. A word may have several targets of one type, and a target
may have several sources. `cut -> cut` can have both `past_tense` and
`past_participle` rows. Self-relations appear once in a combined-direction query.

`usageNote` describes applicability; it is not a machine-readable grading rule.
Confirmation asserts that the relationship is valid under its described usage,
not that it applies to every meaning of the word. No transitive relationships
are inferred. The database validates structure, not linguistic correctness.

## Types

`GET /admin/vocabulary-relation-types` returns an array of `{code, description}`.
The initial codes are:

| Code | Meaning |
|---|---|
| `past_tense` | Past tense |
| `past_participle` | Past participle |
| `ing_form` | -ing form |
| `third_person_singular` | Simple present third-person singular |
| `plural` | Noun plural |
| `comparative` | Comparative |
| `superlative` | Superlative |
| `derived_noun` | Derived noun |
| `derived_verb` | Derived verb |
| `derived_adjective` | Derived adjective |
| `derived_adverb` | Derived adverb |

Codes are case-sensitive. The catalog is code-owned; adding a code also requires
updating the database CHECK constraint through a migration.

## Queries

- `GET /admin/vocabulary/{wordId}/relations`: the word's relations; `direction`
  is `both` (default), `incoming`, or `outgoing`. An unknown word returns 404.
- `GET /admin/vocabulary-relations`: all relations, including pending relations
  and relations involving words in disabled books or with no remaining meanings.
- `GET /admin/vocabulary-relations/{relationId}`: one relation, or 404.

Both list routes accept optional exact `type` and `status` filters, `page`
(default 1, range 1–1,000,000), and `size` (default 20, range 1–100). Invalid
filters or paging values return 400. Lists return `{items, totalCount, totalPage}`,
ordered by `updatedAtUnixMs` ascending, then `id`. Count and rows use one SQLite
read snapshot. Later requests can observe intervening edits; paging is not a
long-lived snapshot.

## Create

`POST /admin/vocabulary-relations`:

```json
{
  "sourceWordId": "existing-source-id",
  "targetWordId": "existing-target-id",
  "type": "past_tense",
  "usageNote": null,
  "confirm": false
}
```

The two IDs and `type` are required. `usageNote` is optional; blank text becomes
null. `confirm` defaults to false. IDs/type are trimmed, but type casing is not
changed. Null characters are rejected. The response is the relation object.

A new relation has version 1, `originKind=manual`, and no origin reference.
Without explicit confirmation it has `needs_review/new_relation` and no prior
confirmation timestamp. With `confirm=true`, it has `confirmed`, a null reason,
and the current confirmation timestamp.

An identical key and note returns the existing row without changing anything,
including its provenance, version, or status. In particular, replaying a create
with `confirm=true` does not confirm an existing row. The same key with a
different note returns 409; use the conditional update route.

## Replace or confirm

`PUT /admin/vocabulary-relations/{relationId}` requires all six fields:

```json
{
  "sourceWordId": "existing-source-id",
  "targetWordId": "existing-target-id",
  "type": "past_tense",
  "usageNote": null,
  "confirm": true,
  "expectedVersion": 1
}
```

The response is the updated relation. A stale version returns 409. Changing the
key to another existing relation's key also returns 409; rows are not merged.
The relation ID, initial provenance, and creation time remain unchanged.

Every successful PUT increments the version and advances the maintenance time
(without moving it backwards if the server clock moves backwards). Changing
the key or note with `confirm=false` sets `needs_review/relation_changed` and
preserves the last confirmation time. An unchanged body with `confirm=false`
preserves the status/reason. `confirm=true` explicitly confirms the current
submitted content and sets the confirmation time. Thus the same route supports
reconfirmation after a word edit.

## Delete

`DELETE /admin/vocabulary-relations/{relationId}?expectedVersion=1` returns
`{"success":true}` inside `data`. A stale version returns 409. A missing relation
is an idempotent success, provided the version parameter is valid. Only the
relationship is removed; neither word is deleted, even if it has no meanings.

Versions supplied for PUT/DELETE must be integers from 1 through
9,223,372,036,854,775,806.

## Batch create

`POST /admin/vocabulary-relations/batch` takes `{"items":[...]}`, containing 1–100
create objects. It returns `{items, created, reused}` in input order. Repeated
items can return the same relation ID. The counts sum to the input length.

All rows are processed in one transaction. A missing word, conflicting note,
invalid item, or storage failure rolls back the entire batch. Item validation,
missing-word and content-conflict messages identify the zero-based input index.
New rows use `originKind=import` and one server-generated batch ID as their
`originReference`; reused rows retain their original provenance. Explicit
`confirm=true` confirms newly created rows only.

## Validation and lifecycle

Create, PUT and batch bodies are limited to 1 MiB, including chunked requests.
They require JSON, reject duplicate/unknown property names, and use exact
camelCase names. Server-owned fields and the removed part-of-speech fields are
rejected. Invalid bodies/codes/versions return 400; oversized bodies return 413;
missing words or relations return 404; concurrent or duplicate-content conflicts
return 409. Cancellation before admission prevents writing. Once admitted to the
existing write transaction, maintenance finishes atomically; losing an HTTP
response does not imply rollback.

A word replacement that changes `Trim().ToLowerInvariant()` spelling marks all
incident relations `needs_review/word_changed`, increments each version once,
and preserves previous confirmation timestamps in the same transaction. Casing,
whitespace, or phonetic-only edits do not invalidate relations. Imports preserve
stored spelling and do not invalidate relations merely by rediscovering words.

Relation foreign keys restrict deletion of either endpoint. Book cleanup removes
selected meanings as before but retains affected words referenced by any relation,
including pending and self-relations. Preview retains `orphanWordCount` as the
number losing all meanings and adds `retainedRelatedWordCount` and
`deletableWordCount = orphanWordCount - retainedRelatedWordCount`. Commit adds
`retainedRelatedWordCount` and reports actual deletions in `deletedWordCount`.
Preview and commit recompute these sets from their respective transaction state.
Clients must use `deletableWordCount` for a deletion estimate; the existing UI
does not yet consume these new fields and can overestimate deletions when it
uses `orphanWordCount`. Backend retention remains enforced.

See [database storage](../database/README.md#vocabulary-relations) and
[deployment and rollback](../development/Deployment.md#vocabulary-relation-upgrade).
