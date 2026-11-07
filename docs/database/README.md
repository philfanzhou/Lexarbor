# Vocabulary database

Vocabulary supports SQLite only. The default database file is `data/vocabulary.db`, and the EF Core model together with the migrations under `src/Lexarbor.Database/Migrations` describes the complete schema. The service has not shipped, so no legacy PostgreSQL migration chain is kept.

## Data-source resolution

SQLite data-source resolution uses ServiceMantle 0.3.0 with the Host content root
as its explicit absolute base directory, independently of the process working
directory. Missing, null or blank configuration keeps `data/vocabulary.db`;
relative paths and `..` segments normalize under that base, and absolute local
paths keep their target. This pure parsing step performs no filesystem writes,
creates no directory/database, and preserves other connection parameters.
The driver's default or explicit 30-second timeout becomes 5 seconds; explicit
values such as 7 or 0 remain unchanged.

Parsing preserves `:memory:` (case-insensitively) and `file:` URI sources. This
does not enable them at runtime: the existing startup gate rejects these
non-file targets before opening a database, and no literal URI filename is
created. `|DataDirectory|` substitutions and invalid input are unsupported and
fail with a safe diagnostic without echoing configuration or provider details.
EF and the startup/migration gate use the same final connection string and
canonical target identity. Paths through symbolic links or mount aliases are
not resolved by parsing. No schema, seed, migration or container `/app/data`
layout changes; rolling back application code neither moves nor rewrites data.

## Core relationships

```text
vocabulary_book (1) <-[RESTRICT]- vocabulary_meaning -[CASCADE]-> (1) vocabulary
vocabulary_book (1) <-[CASCADE]- vocabulary_book_unit
vocabulary_meaning_unit -[(unit_id, book_id) CASCADE]-> (1) vocabulary_book_unit (id, book_id)
vocabulary_meaning_unit -[(meaning_id, book_id) CASCADE]-> (1) vocabulary_meaning (id, book_id)
```

| Table | Responsibility | Key constraints |
|----|------|----------|
| `vocabulary` | Words with British and American phonetics | `word` is unique; `normalized_word` is generated and indexed; `phonetic_uk` and `phonetic_us` are nullable |
| `vocabulary_book` | Book metadata and enabled state | `status=false` means disabled |
| `vocabulary_meaning` | A word's meaning within one book | Both foreign keys are required; the normalized logical key is unique |
| `vocabulary_book_unit` | A unit of a book | `(book_id, number)` is unique within one book; `book_id` cascades on book delete; `(id, book_id)` carries a unique index that serves as the parent key of the membership foreign key |
| `vocabulary_meaning_unit` | The assignment of one meaning to one place of a unit, under one entry kind | `(unit_id, meaning_id, section, entry_kind)` is the primary key — a unit's Section A, Section B, and unsectioned places, each as a word or a phrase, are separate rows — so repeating an assignment is a conflict; `section` is `TEXT NOT NULL DEFAULT ''` with a CHECK limiting it to `''`/`'A'`/`'B'` and `entry_kind` is `TEXT NOT NULL DEFAULT ''` with a CHECK limiting it to `''`/`'word'`/`'phrase'` (the empty strings, not NULL, mean no section and no classification: SQLite treats NULLs in a composite primary key as mutually unequal, so NULL storage could not keep a repeated unsectioned or unclassified assignment from being stored twice); two composite foreign keys both compare the same `book_id` and cascade on delete |

Deleting a word clears its meanings through `ON DELETE CASCADE`. Deleting a book uses `ON DELETE RESTRICT`; when related meanings exist the business layer answers 409 and the administrator should disable the book instead. Deleting a unit removes only the unit and its assignments through `ON DELETE CASCADE`; meanings, shared words, and other units' assignments survive. A book with units but no meanings can still be deleted, and its units follow it away.

Units and unit assignments are created, read, replaced and deleted through `VocabularyBookUnitDomainService` inside the serialized `UnitOfWork` transaction, and the four `/admin/vocabulary-books/{bookId}/units` routes are their management surface: unit numbers must be positive integers and are unique per book — two books may both have a Unit 2 — and a write path naming another book's unit is refused with 409. The per-unit assignment counts the routes display come from one grouped read over the book's memberships, never one query per unit, and count distinct meanings: a meaning assigned to both Section A and Section B of one unit, or under both entry kinds of one place, is still one meaning of it. A meaning may be assigned to several units of its own book, to several sections of one unit, and to several entry kinds of one place; a repeated assignment of the same position is idempotent, a section is exactly `A` or `B` after trimming, and an entry kind exactly `word` or `phrase` — `a` and `Word` are rejected rather than guessed at, and no kind is ever inferred from the entry's text. A cross-book assignment is rejected by the domain service with 409 and is additionally unrepresentable in the database: both membership foreign keys compare the same `book_id` column, so no row can pair book A's unit with book B's meaning. Existing meanings are not required to have any unit assignment, and unit membership never changes a word's or meaning's own book.

The batch import (`POST /admin/vocabulary/batch`) also writes assignments: an entry may name an existing unit of its book through `unitId`, one of its sections through `section` (exactly `A` or `B`, blank for no section, required to come with the `unitId`), and the entry's classification through `entryKind` (exactly `word` or `phrase`, blank for unclassified, required to come with the `unitId`, never inferred), and the meaning it resolves to — created or reused — is assigned to that place inside the same atomic batch transaction. Assignments follow the same invariants as the management routes: units are never created, a repeated assignment of the same position writes nothing, and a `unitId` that is missing or belongs to another book is reported as a per-entry 400 with the batch left unwritten. See [ADR-005](../adr/ADR-005-bulk-vocabulary-import.md).

Because both membership foreign keys reference `(id, book_id)` parent keys, `vocabulary_meaning` carries a unique index over `(id, book_id)` (added by the `AddVocabularyBookUnits` migration; the primary key on `id` already implies its uniqueness). SQLite cannot `ALTER TABLE ... ADD CONSTRAINT` on a table that already exists, so the parent keys are realized as unique indexes rather than table constraints.

The database logical key for an equivalent meaning is:

```text
(vocabulary_id, book_id, lower(trim(coalesce(part_of_speech, ''))), trim(meaning))
```

The last two components are persisted as SQLite stored generated columns and carry a unique index, so duplicate data cannot be produced by going around the application layer.

## First-run creation

The connection string defaults to `Data Source=data/vocabulary.db`. Startup runs the ServiceMantle SQLite startup gate and migration orchestration — the former hand-written initializer is gone, and Lexarbor keeps the rollback-journal normalization ahead of the gate and the executor (EF migrations). The startup order is fixed:

1. the single-instance SQLite deployment mode is validated from the provider declarations alone;
2. the database's parent directory is created when absent — the `data/` directory of a fresh installation, exactly as the previous initializer did;
3. an existing database still carrying the WAL header — a deployment of the earlier releases, or one restored from an old backup — is normalized to the rollback journal through one `Pooling=false` connection: the open replays any committed frames of a leftover `vocabulary.db-wal`, the mode switch checkpoints them into the database, and the close deletes the sidecars, with every committed transaction intact. A missing file is skipped (the next step creates it already in the rollback mode); a normalization that fails stops startup with a fixed safe diagnostic and leaves the file unchanged;
4. the target is observed without being touched: only a file that is missing is created, atomically, by the target-preparation provider — nothing that already exists is replaced;
5. exactly one crash shape is recovered: a target conflict on a file that provably exists (a leftover `vocabulary.db-wal`/`vocabulary.db-shm` after an abnormal shutdown) is replayed and checkpointed once through a `Pooling=false` connection and then observed exactly once more. A conflict that survives that attempt, sidecars without the main file, a file that is not a database, and any other unusable target stop startup with a fixed safe diagnostic (a ServiceMantle error code, never a path, connection string, or SQL) and the file is left byte-for-byte unchanged;
6. the migration orchestration inspects the EF history: an empty or pending database is migrated, an already-current one is skipped without executing anything, and a database whose recorded schema is newer than the running build refuses to start rather than being rewritten;
7. execution applies the pending EF migrations and re-inspects — startup succeeds only when the database is current.

Startup writes no books, words, or meanings, whether the file is new or already exists, so a new database starts empty and data already in an existing one is neither overwritten, duplicated, nor removed. Lexarbor ships no vocabulary data ([ADR-002](../adr/ADR-002-bundled-vocabulary-data.md)); databases created by releases that still loaded the former `Starter English 300` book keep it unchanged.

The gate applies ServiceMantle's strict target rules: the database path must resolve to a plain local file. A path that passes through a symbolic link, a hard-linked database file, a `file:` URI or `:memory:` data source, or a read-only connection mode is rejected before anything is opened, and the diagnostic names the error code only — never the path. Keep the data directory free of symbolic links.

The database file must live on a persistent volume. Docker mounts the host's `data/` at `/app/data` by default; never put a prebuilt `.db` into the image.

## Upgrading an existing database

Migrations are the only startup writer, and each migration states exactly what it changes. `AddVocabularyBookUnits` adds the two new tables, their indexes, and the `(id, book_id)` unique index on `vocabulary_meaning`; it rewrites no existing row, so books, words, meanings, and their query results cross the upgrade unchanged. Existing meanings keep working without any unit assignment. `AddMeaningUnitSections` rebuilds `vocabulary_meaning_unit` — SQLite cannot `ALTER TABLE` a primary key — to add the `section` column with its `''`/`'A'`/`'B'` CHECK and the wider `(unit_id, meaning_id, section)` primary key; every existing assignment crosses the upgrade as the unsectioned place (`section = ''`), keeps its book, and answers the same queries, and the two membership indexes are recreated. Its Down migration merges a meaning's positions of one unit back to the single row the previous key allows, so rolling back loses the section split but no assignment. `AddMeaningUnitEntryKinds` rebuilds the table once more to add the `entry_kind` column with its `''`/`'word'`/`'phrase'` CHECK and the four-column `(unit_id, meaning_id, section, entry_kind)` primary key; every existing assignment crosses the upgrade as the unclassified kind (`entry_kind = ''`) with its section and book unchanged, and the membership indexes are recreated. Its Down migration merges a meaning's kinds of one place — one row per `(unit_id, meaning_id, section)` survives with its section intact — so rolling back loses the kind split but no assignment. Back up the database consistently before upgrading (stop writes first, or use a SQLite online-backup tool), as with any release; the table rebuilds rewrite the membership table, so a backup taken before the upgrade is the only way back once a sectioned or kinded assignment has been written.

The startup gate also performs an upgrade check an operator can rely on: a database whose `__EFMigrationsHistory` records a migration the running build does not know (for example a database already migrated by a newer image) stops startup with `migration.version_too_new` and is left untouched. To preview the check against a copy of the database, compare the newest `MigrationId` in `__EFMigrationsHistory` with the migrations of the target image — the running build migrates only forward, never down.

## Write consistency

- A new meaning must reference a book that exists and is enabled.
- When an update carries a word, book, or meaning ID and that object does not exist, the answer is 404; it must never become an insert.
- Updating a meaning must confirm the meaning belongs to the current word and book.
- A word has a display spelling and a normalization key, and they are not the same value. The key is `word.Trim().ToLowerInvariant()`; the display value is the imported spelling, trimmed but with its casing, so `Nobel Prize` is stored as `Nobel Prize`. The database keeps the same expression as the generated column `normalized_word` (`lower(trim(word))`), and the lookup that resolves an import to an existing word compares against it, so the comparison is an index seek rather than a scan of the table. The column is virtual rather than stored, because SQLite refuses `ALTER TABLE ... ADD COLUMN` for a stored generated column and the column has to be addable to a database that already exists; the index holds the computed value either way. It carries no unique constraint: asserting one would fail on a database that already holds two spellings of one word. `Apple`, ` apple `, and `APPLE` resolve to one row; the first creation's spelling is the stored display value, a later equivalent import never rewrites it, and only the administrator's word replacement can correct casing while the normalized key is unchanged. Rows lower-cased by an earlier release keep their value.
- Re-importing the same word, book, normalized part of speech, and definition is idempotent.
- A SQLite deployment is single-instance only; a process-level write transaction lock serializes administrative writes, and the database's unique index is the last line of defence.
- `UnitOfWork` maps SQLite constraint errors to 409 and never exposes the internal error to the client.

## Shared word replacement

The administrator word PUT uses `VocabularyWordEditService` within the existing serialized UnitOfWork. It reads the current word from SQLite without relying on tracked values, then checks for any other ID with a parameterized `EXISTS` query. A connection-local SQLite function applies .NET `Trim().ToLowerInvariant()` to stored spelling, including historical non-ASCII capitals and Unicode whitespace. The generated `normalized_word` column uses SQLite ASCII case folding and space trimming, so its index cannot answer this complete check. The edit check scans stored spellings until a conflict is found without materializing vocabulary entities; its cost can grow with the catalogue. The target ID is excluded, while all other historical duplicates count as conflicts. No new uniqueness constraint or migration is added. The word fields and any incident relation invalidations change together; meaning rows and their ownership are untouched. The submitted spelling is stored as the display value, trimmed but with its casing — this is the explicit spelling replacement path, and its conflict check rejects another word with the requested normalized key. Explicit null/blank phonetics clear stored values, unlike import merging. Constraint and busy failures retain the existing atomic rollback and HTTP mappings.

The word replacement transaction also invalidates every incident vocabulary
relation when .NET `Trim().ToLowerInvariant()` spelling changes. It sets
`needs_review/word_changed`, increments each version once (including self-relations),
and preserves the last confirmation time. Case/whitespace and phonetic-only edits
do not invalidate relations. This side effect commits or rolls back with the word.

## Meaning replacement

`VocabularyMeaningEditService` uses the existing repositories and serialized UnitOfWork to validate all three resources and ownership before changing a meaning. ID reads fetch current database values without returning earlier tracked snapshots. The existing equivalent-meaning key rejects any other matching meaning ID; book/word foreign keys never move, including in disabled books. Only that meaning's content fields and updated timestamp change. This API does not create definitions or alter import merge semantics, constraints, or schema. SQLite constraint/busy exceptions retain the existing rollback and status mappings.

The administrator PUT now composes this text replacement with the existing
specific-phonetic boundary through `VocabularyMeaningPhoneticEditService` on the
same scoped UnitOfWork/context. Old text-only requests preserve both specific
columns. Provided accents check their old epoch/source/own-binding conditions
inside the writer before text replacement changes source input. Two accents,
text, exact deletion authority, invalidation and pending work all roll back
together. The scheduler refreshes work against final committed text. Reads batch
the page's epochs and own asset IDs within the same deferred snapshot as the
fields; no per-meaning query or schema duplication is introduced. See the
[administrator contract](../api/MeaningPhonetics.md).

## Exact meaning-position writes

Exact-position management writes use the existing four-part key `(unit_id, meaning_id, section, entry_kind)` through one serialized `UnitOfWork` transaction. A move validates the source and target units and meaning belong to the path book, rejects an existing distinct target, then deletes the exact source and adds the target atomically. A delete removes only the specified assignment; neither operation cleans orphan meanings or shared words. SQLite's primary key and paired book foreign keys remain the final uniqueness and ownership checks. No migration or new table is required.

## Administrator read snapshots

The phrase-position administration read filters `entry_kind='phrase'` in SQL and counts assignment rows directly. Its `totalCount` and pages include separate rows for the same meaning in different units or sections while ignoring a sibling `word` assignment at the same unit and section. The query pages by unit number, stored section, spelling, unit ID and meaning ID, and projects only the requested page within one deferred SQLite snapshot. Disabled books are readable; missing or cross-book units return 404. No schema change is required.

`VocabularyAdminQueryRepository` implements the separate management query contract without the public enabled-book filter. Each response opens a SQLite deferred read transaction and enlists the EF context. It does not acquire `UnitOfWork`'s write semaphore or reserve a write lock. Under the rollback journal a concurrent writer's commit queues behind the open read transaction, and the response still answers from its original snapshot.

Filtering, distinct-word counting and paging run in SQL. Only the current page's word IDs are used to batch-load deduplicated book memberships and, where requested, applicable meanings. Summaries do not load meanings; content never loads other books' meaning text. Detail loads also batch-read the page's meaning-level unit assignments (join through `vocabulary_meaning_unit`) in one query, never one per meaning. The unit-content route (`GET /admin/vocabulary-books/{bookId}/units/{unitId}/content`) uses the same snapshot and paging; its membership probe narrows a word through a meaning of the book assigned to the unit, its `wordCount`/`meaningCount` count the unit rather than the book — still per distinct meaning, so a meaning in both sections of the unit or under both kinds of one place counts once — and a missing or cross-book unit answers 404. An optional `section` query parameter (`A`, `B`, or `none`; anything else answers 400) narrows the page and those counts to that section's places, and an optional `entryKind` query parameter (`word`, `phrase`, or `none`; anything else answers 400) narrows them to that kind's places — the two are independent dimensions of one position and combine by intersection — while the response's `sectionCounts` (`sectionA`, `sectionB`, `noSection`) and `entryKindCounts` (`word`, `phrase`, `none`) always report the whole unit's place counts whatever the filters. Each meaning's unit assignments name the place they sit in (`section` null for the unsectioned place, `entryKind` null for the unclassified kind). Existing `IX_vocabulary_word`, `IX_vocabulary_meaning_book_id_vocabulary_id`, and the `vocabulary_meaning_unit` primary key over `(unit_id, meaning_id, section, entry_kind)` support ordered traversal and membership probes. No schema or index migration is needed. Read responses promise consistency within one request, not between pages.

## Scoped cleanup

The new cleanup commands are separate from the legacy book DELETE, which still refuses a book with meanings. `VocabularyCleanupService` validates the current book, ownership, selected memberships and optional exact name within the same UnitOfWork write transaction as deletion. Preview instead uses a deferred read transaction without the process write semaphore.

Let R be the meanings selected by book and action, and A the distinct word IDs in R. One shared parameterized predicate defines R in preview counts and commit. Commit records A in a connection-local temporary table, deletes R, deletes only words in A with no remaining meaning and no incoming or outgoing vocabulary relation, then optionally deletes the book. Pending and self-relations also retain their endpoints. Other/disabled books' references and unrelated historical orphan words remain untouched. No cascade is used to widen the scope, and no whole book of meaning entities is loaded. The temporary table is dropped in `finally`; tracked entries are cleared after bulk SQL so later operations cannot reuse deleted entities. Failure at any stage rolls back all business rows.

Preview keeps `orphanWordCount` as the number losing all meanings, adds
`retainedRelatedWordCount`, and computes `deletableWordCount` as their difference.
Commit adds `retainedRelatedWordCount`; `deletedWordCount` remains the actual
number deleted. Both phases use the same retention conditions, evaluated at the
time of their own transaction. Removing a relation never sweeps newly unreferenced
words.

A single instance remains the supported deployment. SQLite serializes external writers; busy/constraint failures keep existing 503/409 mappings. Preview counts can become stale between requests. These destructive commands offer no undo, replay safety or automatic orphan sweep.

## Vocabulary relations

`20261105011010_AddVocabularyRelations` adds the empty `vocabulary_relation`
table and its indexes; existing catalog rows and tables are not rewritten.
Its thirteen columns are `id`, `source_word_id`, `target_word_id`, `type`,
`usage_note`, `status`, `review_reason`, `origin_kind`, `origin_reference`,
`version`, `created_at_unix_ms`, `updated_at_unix_ms`, and
`last_confirmed_at_unix_ms`. It has no book, meaning, or part-of-speech columns.
See [the relation API](../api/VocabularyRelations.md) for field and maintenance
semantics.

The three non-null identity columns `(source_word_id, target_word_id, type)`
have one unique index. Two foreign keys reference `vocabulary.id` with RESTRICT
on deletion (and explicit RESTRICT on parent-key updates in the migration).
Application connections must enforce foreign keys. Self-relations are valid.
CHECK constraints enforce the fixed type/state/reason/origin codes, bounded notes
and references, a positive integer version, consistent confirmation state, and
ordered nonnegative integer UTC timestamps. They do not validate linguistic
correctness or maintain versions automatically.

Outgoing and incoming indexes start with the corresponding word ID, then type,
status and relation ID. A partial `(updated_at_unix_ms, id)` index contains only
`needs_review` rows. Lists count and page in one deferred read transaction.
Mutation reads are untracked/current and occur inside the existing serialized
UnitOfWork. Conditional UPDATE/DELETE predicates include the expected version;
batch creation uses one transaction. Provenance and creation time are immutable
through the APIs. The table stores current state, not an edit-history ledger.

Downgrade drops this table and its indexes, losing all relationship data while
preserving catalog rows. Coordinate the migration with the updated cleanup and
word replacement code; see [deployment](../development/Deployment.md#vocabulary-relation-upgrade).

## Meaning submissions

`AddMeaningSubmissions` adds only the independent `meaning_submission` table. A row is a definition an integration client proposed: `word`, `part_of_speech`, `meaning`, `example`, the client `source`, a `status` (0 pending, 1 approved, 2 rejected, enforced by `CK_meaning_submission_status`), the `result_*` location and text once approved, and Unix-millisecond `created_at_unix_ms`, `updated_at_unix_ms` and `reviewed_at_unix_ms`. Stored generated columns `normalized_word` (`lower(trim(word))`), `normalized_part_of_speech` (`lower(trim(coalesce(part_of_speech, '')))`) and `normalized_meaning` (`trim(meaning)`) use the catalog's own normalization. The filtered unique index `IX_meaning_submission_pending_key` allows one pending row per normalized definition; `IX_meaning_submission_status_created_at_unix_ms` serves the review queues. The table has no foreign key to the catalog: an approved row keeps its result IDs even if the meaning is later edited or deleted. Approval writes the meaning through the import path and updates the row in one `UnitOfWork` transaction.

## ServiceMantle Data Protection keys

`AddServiceDataProtectionKeys` adds only the independent
`service_data_protection_keys` table (composite primary key `service_id`,
`key_id`; payload column `encrypted_xml`). Rows hold ASP.NET Core Data
Protection key XML as ServiceMantle `sm:v1:` authenticated envelopes scoped to
the `lexarbor` service id and protected by the deployment root key
(`DataProtection:RootKey` or the root-key file). No vocabulary row or other
table is touched, and no plaintext key XML ever reaches the database file.

The table is written by the ServiceMantle repository through the registered
`IDbContextFactory<VocabularyDbContext>`. A wrong root key, a damaged row, or a
row copied from another service fails closed: startup stops with a fixed safe
diagnostic instead of serving unreadable sessions. Migration Down drops only
this table, invalidating administrator sessions. See
[Deployment](../development/Deployment.md#data-protection-key-storage) for the
root key, backup and upgrade contract.

## Encrypted administrator session storage

`AddAdminSessions` adds only the independent `admin_session` table and the
`(expires_at_unix_ms, handle_hash)` expiry index. It does not change any vocabulary
row, relationship, seed or existing migration. The primary key is the lowercase
hex SHA-256 digest of a canonical 32-random-byte base64url handle; the original
handle is never persisted. `expires_at_unix_ms` is an INTEGER UTC Unix millisecond
value. `protected_payload` is Data Protection ciphertext under `Lexarbor` /
`Lexarbor.AdminSession.v1`, containing versioned, independently validated token and
identity data bound to that exact hash and expiry. Database code never handles
plaintext tokens or identity. This is an internal Host storage facility, also used by the opaque session
authentication scheme. Password login still issues the legacy JWT cookie; public
HTTP contracts and frontend behavior are unchanged. See [session authentication](../development/Deployment.md#administrator-session-authentication)
for cookie precedence, CSRF, local logout and authentication rollback.

Trusted Host callers must validate token signatures, issuer, audience, access-token
`exp` and administrator role before creating or replacing a session. The store
checks structure and uses that verified expiration; it does not authenticate an
arbitrary JWT, trust `expires_in`, refresh tokens or slide the expiry. Each read
queries current SQLite values and rejects `now >= expiry`, missing records,
corrupt ciphertext or model, mismatched metadata and unavailable keys. Database
failures remain distinct safe storage errors (busy retains the 503 direction).
They are not treated as a missing identity or a fallback authentication result.

Create, replace, revoke-and-read and cleanup use the existing UnitOfWork write lock
and transaction shared with vocabulary writes. Handles/snapshots are returned only
after confirmed commit. Nested session writes inside another transaction are refused
before writing, so they cannot return an uncommitted handle or snapshot. Replace inserts a fresh independently verified session and
deletes the old handle atomically; failure/cancellation before commit retains the
old row. A missing or expired old handle does not prevent independent creation.
Concurrent replacements can create distinct new sessions. Revoke deletes even
expired/corrupt records; at most one concurrent revoke returns a valid snapshot.
New reads after confirmed revoke fail; already-started requests retain their earlier
snapshot. Cancellation before commit begins rolls back; after commit begins a lost
result has an unknown outcome and Create/Replace must not be automatically retried.

Cleanup runs every minute in a fresh scope, deleting at most 100 expired records in
expiry/hash order per batch. Cleanup failure emits a fixed safe diagnostic and
retries the next minute; it is neither a readiness prerequisite nor the mechanism
that enforces expiry. `Database:InitializeOnStartup=false` remains supported without
cleanup failure stopping the host. No session payload or SQL/provider error detail
is logged by the Host session operations.

Back up the database and complete key ring together confidentially. Possession of
both, or runtime-user/root access, can decrypt sessions; this does not provide
multi-instance or upstream/global token revocation. **After restoring any historical
database backup, before starting the application, run `DELETE FROM admin_session;`
on the restored database if the table exists and require fresh login.** This avoids
reviving sessions revoked after that backup. Code rollback can ignore the additive
table; retain it and the ring for a later upgrade, which can read still-valid rows.
Migration Down drops only `admin_session`, discards all sessions and requires fresh
login, leaving vocabulary data unchanged. See [deployment and recovery](../development/Deployment.md#encrypted-administrator-session-storage).

## Management audit log

`AddServiceAuditLogs` adds only the independent `service_audit_logs` table — the
ServiceMantle management audit model with SQLite-dialect byte-length CHECK
constraints (built on `octet_length`, provided by SQLite 3.45 and newer; the image
bundles 3.53) and six keyset-pagination indexes over `(occurred_at_utc, id)`
combinations. No vocabulary row, session row or existing migration is touched, and
existing databases take the migration through the ordinary startup pending-migration
path.

Rows are written only through the ServiceMantle audit event model, whose
sensitive-content policy is an enforced boundary: values that look like secrets are
redacted or rejected before reaching SQL. Lexarbor writes exactly the three
administrator authentication events (`admin_login.succeeded`,
`admin_login.failed`, `admin_login.logout`); sign-in and logout rows join the same
serialized transaction as their session write, and the failed-login row is saved
standalone under the same storage failure semantics. No authorization code, state
value, token, client secret, session handle or provider error text is ever stored.

The table has no automatic retention, cleanup or query endpoint in Lexarbor: it
grows with login traffic, and backup plus pruning are the operator's
responsibility. Migration Down drops only this table, losing the audit history and
nothing else. See
[Deployment](../development/Deployment.md#management-audit-log) for the event,
field and retention contract.


## Audio reference model

`AddVocabularyAudio` adds `audio_asset`, `vocabulary_audio_binding`, and
`audio_upload_operation`, plus the unique `(id, vocabulary_id)` index on
`vocabulary_meaning`. It does not rebuild existing tables or modify any existing
word, meaning, book, position, administrator session, audit, or Data Protection row.
The internal model, selection reads and conditional write repository do not enable
HTTP audio uploads, background cleanup, or S3 access by themselves.

Assets hold an independent ID, bucket, server-owned object key, SHA-256, byte length,
MIME, creation time and nullable first-publication time. A binding stores a word,
optional meaning, locale (`en-GB` or `en-US`), asset and source revision. Partial
unique indexes allow one default per `(word_id, locale)` and one override per
`(meaning_id, locale)`. A composite foreign key `(meaning_id, word_id)` verifies
meaning ownership. Word/meaning deletion, including bulk cleanup SQL, cascades
only bindings; assets and upload operations remain. Asset foreign keys restrict
removal while a binding or recovery operation still references them.

Operations have a canonical UUID key, SHA-256 request fingerprint, historical
target and conditions, independent asset reference, upload lease owner/fence/expiry,
and state. They retain a separate terminal receipt (status, asset/revision or safe
error code, completion time) and cleanup lease/fence/return state, attempts, next
attempt, last safe error code and observation time. UTC lease and lifecycle times
are INTEGER Unix milliseconds for conditional comparisons. CHECK constraints limit
locale/state, valid receipt shapes, positive upload/cleanup lease fences and hashes.
Target IDs intentionally have no foreign key: deleting a target must not erase the
known object location or historical request result. `AudioUploadRepository` enforces runtime conditional transitions, receipt
immutability and the prohibition on cleaning published assets. Static schema
checks alone are not a runtime upload or recovery implementation.

`AudioReadRepository` reads current word, owned optional meaning and both accents'
bindings/assets inside one deferred SQLite read snapshot, without taking the
UnitOfWork write semaphore. Reads ignore tracked entities. All consumers use
`AudioSelectionService`: source revision is lower-case SHA-256 of the fixed JSON
array `["lexarbor.audio.source.v1", word, locale, phonetic, targetKind, partOfSpeech,
meaning]`, using System.Text.Json UTF-8 encoding with explicit nulls. `targetKind`
is `default` or `meaning`; default input has null part of speech and meaning.
Display spelling retains casing. Only the locale's phonetic is included; meaning
targets use their nonblank specific phonetic or inherit the default and
additionally include their part of speech and definition. Examples,
IDs, timestamps, positions and book enablement do not affect the revision. Reading
actual current input covers word/meaning replacement and batch import without
changing their existing write results.

`AddMeaningPhonetics` adds nullable `phonetic_uk` and `phonetic_us` columns to
`vocabulary_meaning`, mapped through the internal meaning model and audio input.
It does not fill them, call AI, delete assets or alter existing IDs, default
phonetics, bindings or upload receipts. Null/blank means inheritance. Default
targets and inherited meaning targets retain the exact `source.v1` encoding and
revision; a nonblank specific value replaces only that meaning/accent's raw
phonetic input. Explicit SQL entity projections include both new columns.

A request without a meaning selects only its default. A meaning with a blank
specific phonetic also selects only the default, even with a retained legacy
override. A nonblank specific phonetic selects its own binding, including stale
audio; without one it can fall back only when its nonempty normalized phonetic
equals the default's. Otherwise it is missing. Comparison uses NFC, outer
whitespace trimming and removal of exactly one matching `/.../` or `[...]` pair,
then ordinal comparison; stress, phonemes, length, case and internal spaces remain.
A fallback's revision is compared with default input, without normalizing the
revision's raw input.
There is no cross-accent fallback. No selected binding means `missing`; matching
revision means `ready`; otherwise `stale`. Own and effective bindings remain
separate so later upload conditions cannot accidentally use a fallback asset.
These are content states, not proof that an object exists or a playable URL.

Back up SQLite and the independent audio bucket consistently. The phonetic
migration and existing manual replacement/unbind operations retain published
objects, including legacy overrides that are no longer selected. Historical
SQLite backups may reference them, so retain matching remote backups for the
required restore window. This migration performs no object deletion.
A database restore does not restore missing remote files, and a bucket change
requires an explicit object migration.

The existing startup version gate still refuses an older image if it does not
know the new migration (`migration.version_too_new`). Keeping additive tables does
not itself make that image start. Before an explicit Down, stop audio activity and
back up SQLite and S3; use the current migration tooling to return to
`20261001125645_AddServiceAuditLogs`, then deploy the matching older image. Down
removes only these three new tables and the new meaning index; it does not change
existing rows or touch S3. Preserve the pre-Down backup to retain audio bindings,
receipts and recovery metadata. See [deployment](../development/Deployment.md#database)
for the existing startup gate and [backups](../development/Deployment.md) for recovery.

For a rollback of `AddMeaningPhonetics` alone, stop writes and preserve SQLite and
S3 backups, then explicitly migrate to `20261031172216_AddMeaningSubmissions`
with tooling that knows the new migration. Down drops the two new phonetic
columns and loses any specific values; old fields, assets, bindings and receipts
remain. An older image still refuses the upgraded history with
`migration.version_too_new`; additive columns do not bypass that gate. Restoring
the pre-upgrade backup is the alternative when those values must be retained.


## Conditional audio persistence

`IAudioUploadRepository` is the network-free persistence boundary shared by upload,
logical deletion and recovery callers. Each mutation owns a short SQLite write
transaction through `UnitOfWork`; it may not be called inside an enclosing write
transaction. SQLite writer admission and conditional owner/fence updates arbitrate
across connections; the process semaphore only queues local writers. Reads use
fresh, untracked database values rather than pre-upload tracked targets.

Upload callers validate authorization, bounded body, MP3, locale and fingerprint
before `ReserveAsync`. Supply a canonical UUID, the immutable request fingerprint,
current source revision, **the target's own** expected asset (null for no binding),
SHA-256/size and trusted bucket/key prefix from validated server configuration.
The repository creates a new asset ID and random `{prefix}{UUID-N}.mp3` key; it
never accepts an object key or URL from the client. The default prefix is `audio/`.
Key prefixes consist of one to four lower-case alphanumeric/hyphen directory
segments followed by `/`. Producer/voice and safe error codes are caller-validated,
bounded metadata; never pass credentials, SDK exception text or signed URLs.

Call order is reserve, begin transfer, **Put outside any database transaction**,
then complete. A reservation creates an owner/fence/expiry lease (normally five
minutes); `RenewAsync` only extends a still-valid lease owned by that owner/fence.
Expiry is exclusive: a lease expiring at `now` is already invalid. The repository
re-reads its injected `TimeProvider` after writer admission, so time spent queued
cannot let an expired owner publish using an earlier caller timestamp. A transfer
cannot be restarted or published while merely Reserved. Complete atomically
re-reads word/owned meaning, source revision, own asset and the lease. A match
writes the binding, first-publication stamp and 200 receipt in one transaction;
a missing target or changed condition writes an immutable 404/409 failure receipt
without changing the old binding. Failure/cancellation before publication uses
`AbandonAsync` with a safe 404/409/503 receipt. Recovery uses `ExpireAsync` for an
expired Reserved/Transferring operation, recording fixed 503 `audio.upload_expired`.

A repeated key with a different fingerprint conflicts. With the same fingerprint,
a Reserved/Transferring operation reports InProgress (409); any terminal operation
replays its original receipt, including while cleanup is running. A successful
operation never publishes again, even if its binding was subsequently replaced
or removed. `UnbindAsync` checks current target ownership and only removes that
locale/target's own matching asset; it returns Conflict for a mismatched asset,
NotFound for a missing target or own binding, and Removed for a successful removal. It never
changes operation receipts or deletes assets/objects. The HTTP caller maps these
internal results to its endpoint contract.

Cleanup first calls `ClaimCleanupAsync` on Abandoned/CleanupObserved records.
The database requires no valid upload lease, at least 24 hours since reservation,
a due retry time and an asset that has **never** been published. The claim records
an incrementing fencing token and cleanup expiry, preventing upload publication.
Only the valid cleanup owner/fence may report success via `ObserveCleanupAsync`
or failure via `ReleaseCleanupAsync`. Remove/Stat run outside the transaction.
Successful removal or confirmed absence becomes CleanupObserved; failures restore
Abandoned or the previous CleanupObserved state and schedule another attempt.
`ExpireCleanupAsync` restores the same previous state after cleanup-lease expiry,
recording `audio.cleanup_expired`. Every retry preserves the original receipt,
key, attempt count and observation history. Retain these records after deletion:
a timed-out Put may arrive late, so CleanupObserved is checked again rather than
being considered permanently erased. `FindRecoverableAsync` returns a bounded,
ordered advisory list; callers must still win the corresponding conditional
operation before touching S3. It neither claims work nor extends a lease.

Cancellation while waiting for writer admission leaves no change. Once admitted,
the short transaction completes without request cancellation, following the
existing vocabulary commit policy; a lost response is recovered by reading the
idempotency key. No lease guarantees a distributed SQLite/S3 transaction, immediate
object reclamation, or protection against direct administrative object deletion.
This boundary performs no network calls and does not change vocabulary editing,
batch-import, authentication, HTTP routes, or automatic startup migration policy.


## Failed-upload recovery execution

`AudioRecoveryService` and the host's `AudioRecoveryWorker` execute the existing
conditional transitions above. There is no extra schema migration or alternate
state machine. The worker uses a fresh scoped SQLite context for each bounded batch;
Remove/Stat run only after a successful database cleanup claim, outside write
transactions. Configuration defaults, retry intervals and restoration steps are in
[deployment recovery](../development/Deployment.md#failed-upload-recovery).

Never prune `audio_asset` or `audio_upload_operation` merely because no current
binding references them. First-publication stamps prevent failed-upload cleanup from deleting published
objects. The explicit specific-phonetic invalidation authority below is a separate,
limited exception; absence of a binding alone never grants deletion authority.
CleanupObserved retains the original immutable failure receipt and object location
and schedules another check even after a successful Remove, covering late remote
writes. A restart expires abandoned upload/cleanup leases conditionally and resumes
persisted retries; it does not rebind an old request, scan S3, clear a bucket or authorize deletion of
published objects. Preserve these tables and the private bucket together when
backing up or restoring the database.

## Retired textbook recording schema

`RemoveTextbookAudio` removes the recording catalog and upload-operation tables,
including the publication-retention trigger on `audio_asset`. Vocabulary data,
pronunciation bindings and upload receipts, and all shared audio assets remain
unchanged. The retained object locations still protect the storage destination.
No S3 operation runs during migration. Fresh installations run the retained
historical migrations followed by this removal and end with the same schema as
upgrades. Downgrading recreates empty tables and publication triggers only; restore
a pre-upgrade backup to recover recording metadata and receipts. See
[deployment and rollback](../development/Deployment.md#textbook-recording-removal).


## Enrichment task persistence

`AddEnrichmentTasks` adds `enrichment_target`, `enrichment_round`,
`enrichment_call` and `enrichment_wait`. It does not rewrite vocabulary, assets,
bindings or permanent upload receipts, and does not start a worker or contact a
provider. No public HTTP route, authentication policy or browser contract changes.

A target is unique by word, optional owned meaning, accent and step (`Prepare`,
`Fields`, `Audio`). Separate partial unique indexes cover default and meaning
identities so SQLite NULL semantics cannot create duplicate default work. Empty
specific phonetics mean inheritance: no meaning generation target is created for
them, and meaning-specific field filling is not a step. Each accent/step has its
own round and budget; a successful step or accent remains recorded when another
fails. Plans contain only bounded spoken text, pronunciation hints and proposed
fields needed for recovery. Raw provider responses, credentials, key fingerprints,
object locations and signed URLs do not belong in this history.

Rounds capture current input, the relevant opaque durable configuration version,
failure budget (default five including the first failure), request count, consumed
failures, plan, safe error/wait reason, next attempt and owner/fence/expiry. Input
uses the shared audio source revision; preparation/fields also include explicit
position kinds and current definitions/parts of speech. Audio guards compare the
target's **own** asset, never a default playback fallback. Other accents' phonetics
and configuration versions do not reset this target. A normal scan, restart,
service recovery or budget setting change preserves its existing round and
exhaustion. A changed relevant input/configuration or a distinct explicit single
retry opens a new round; retry UUIDs are deduplicated across retained history.
Already published unchanged audio and completed fields are not reopened by configuration
changes or explicit retries. Preparation refreshes when its relevant text/field-permission
version changes or a legacy content-only plan lacks its required envelope.
Completed rounds and old calls are retained. A same-round successful field/binding
write advances expected input rather than creating another round.

`BeginCallAsync` durably records each dispatch immediately before the external
request, using a fresh request ID and, for an audio attempt, a fresh upload
idempotency key. A `Started` call has an unknown outcome after a crash: it is not
proof of failure or permission to send again. Counts describe recorded request
dispatches, not guaranteed exactly-once provider billing. Call completion is
idempotent. 429 and HTTP-200 business throttling record a request and a wait, but
consume no failures. A transient actual failure consumes one; the fifth becomes
failed and cannot be claimed again after service recovery. Unsupported capability
or invalid configuration can stop without making a request. Disabled, missing
configuration/storage and service-pause waits consume neither requests nor failure
budget. Every wait event retains its reason, time and next attempt separately from
the latest round state. Network I/O and waiting occur outside writer transactions.

`ClaimAsync` atomically admits only one owner and increments its fencing token;
expiry is exclusive. It can reclaim an expired running round without clearing its
plan, calls or budget. Time is read from `TimeProvider` after SQLite writer
admission, including the process write queue. Old owners, fences, rounds, epochs,
changed input, changed own bindings and deleted targets cannot complete or mutate
queue work. `ValidateInTransactionAsync` is the shared authorization boundary for
later field and final binding writers: it requires the **same scoped context and
actual serialized UnitOfWork transaction** as the mutation. A standalone or prior
transaction check cannot authorize a later write. After a successful conditional
mutation, `AdvanceInputInTransactionAsync` accepts its new input only when the same
transaction validated the old guard; a failed check must abort the mutation and
roll back. These ports do not themselves implement field filling or audio upload.
The separate upload lease continues to protect upload operations. Guarded binding
publication uses `IAudioGuardedPublicationRepository.CompleteGuardedAsync` to
check both authorities inside its actual short writer transaction.

Relevant content changes use `InvalidateInTransactionAsync` with the content edit
in that same transaction. It advances a persistent epoch independently of hashes,
so A-to-B-to-A cannot revive an old executor. Clearing a specific phonetic cancels
its active work without generating a new specific value. Deletion triggers cancel
active rounds and tombstone target identities on word/meaning deletion, including
cascading cleanup, while retaining round/call/wait history. Discovery cannot
resurrect those deleted identities.

A committed upload with a lost task-completion response is reconciled through
`RecoverPublishedAsync`: it requires that round's recorded upload key, the
permanent successful upload receipt, unchanged current input and the exact current
own binding. It then records success without another request. A receipt from an
older round, another target, changed input or a replaced/unbound asset cannot
complete current work. Unpublished upload cleanup remains on the existing upload
recovery path; this migration grants no authority to delete published objects.

The single-target executor extends existing `plan_json` with optional internal
`Preparation` metadata: original generation input, expected input version, opaque
text version, entry kind/classification basis, mandatory human IPA, pronunciation
control and whole-target speech IPA. The complete JSON still has the existing
16,384-character limit; no schema migration, backfill or public JSON field is added.
Old plan JSON remains readable but cannot authorize TTS without fresh preparation.
Speech IPA is not a catalog field candidate. Audio rounds use the stable text version
plus the current accent's TTS version; preparation also captures whether fields may
be filled. No credentials or configuration digests are added to task history.

`EnrichmentExecutionRepository.CommitFieldsAsync` owns the actual short UnitOfWork
writer transaction, validates the guard on the same scoped context, rereads own
binding and missing-field permissions, then advances the same round input and plan
atomically. A failed final advance rolls back the fields as well. POS equivalence
conflicts are saved as bounded meaning IDs in plan metadata; only the field step
stops, without deleting or merging IDs. Upload binding still uses the existing guarded
publication transaction and permanent receipt recovery. Local admission throttling
records a wait without fabricating a supplier call. The single-target executor is
now called by the separately persisted autonomous scheduler below. Downgrading software without schema changes is still unsafe
for in-flight work that the older executor cannot interpret: stop execution and preserve
consistent database/configuration/object backups before changing versions.

Back up the full SQLite database and persistent configuration together, plus the
matching private audio objects. The startup version gate continues to reject
unknown migration history in older images. An explicit Down to
`20261101125550_AddMeaningPhonetics` drops these four tables and the deletion
triggers, losing all queue rounds, plans, counts and history; it preserves old
vocabulary/audio data and upload receipts and performs no S3 operation. Stop writes
and retain a pre-Down backup, or restore a matching pre-upgrade backup before
running an older image. See [deployment](../development/Deployment.md#enrichment-task-upgrade).


## Specific-audio invalidation recovery

`AddAudioInvalidations` adds one initially empty `audio_invalidation` table and
seven safety triggers. It preserves every existing ID, default phonetic, asset,
binding, upload receipt and enrichment round; it creates no historical authority
and performs no S3 operation. The table records the exact asset/bucket/key,
word/meaning/accent, `specific_phonetic_changed` reason and authorization epoch,
plus owner/fence/expiry, attempts, next retry, safe error and observation times.
Target IDs have no foreign key so deletion cannot erase the authority. Its asset
foreign key restricts removal. Locations are internal and never public task data.

`AuthorizeInTransactionAsync` requires the caller's actual serialized writer
transaction on the same scoped context. Call it while the exact old **specific**
binding still exists, then remove that binding in the same transaction. It does
not commit, edit phonetics, invalidate enrichment work or contact storage. The
shared phonetic-edit boundary below owns those coordinated actions. A default
asset, arbitrary orphan, wrong target/accent, or unpublished asset cannot be
independently authorized. A matching repeat is idempotent. A later edit of another live specific binding
sharing that same asset reuses its retained authority without replacing the
original target/epoch; each edit still unbinds only its own reference. Without a
matching live binding, different target/epoch data cannot grant new authority.

Claiming rechecks the exact published asset/location and absence of **all** current
bindings under SQLite's writer transaction. Current default, other meaning or
other accent references defer deletion without consuming an attempt. Once a claim
wins, the permanent `delete_started_at_unix_ms` tombstone prevents INSERT or UPDATE
of any new binding to that asset, including direct SQL and future publication
paths. A competing writer either commits a reference first (claim waits), or is
rejected after the tombstone commits. Triggers also protect claim eligibility,
immutable authority/location and retention. Upload publication checks the tombstone
in its own transaction. Success receipts remain replayable and never restore a
binding. Failed-upload cleanup retains its original never-published requirement.

`AudioInvalidationRecoveryService` shares the worker's captured S3 generation,
controlled UUID-MP3 path guard, timeout, lease and exponential retry settings.
Remove/Stat happen after claim commit, outside SQLite transactions. Unknown Delete
responses are checked by Stat; an absent exact key can be observed, while failed
Stat or a remaining object retries. Cancellation/timeout never counts as observed;
expired claims retain the tombstone and use a new fence. Observed records and
receipts stay permanent and are rechecked, including after restart. Attempts have
no finite cutoff and are separate from enrichment's five-failure budget.

The worker processes a bounded batch from each queue per interval, regardless of
enrichment switches. Disabled audio starts no recovery DB/S3 work; saved enablement
resumes persisted work. Authority/observed records also block storage destination
changes, even if an external writer bypassed asset foreign keys.

An explicit Down to `20261101163649_AddEnrichmentTasks` drops only this table and
its seven triggers, losing all deletion authority, retries and tombstones. It
preserves vocabulary, assets, bindings, enrichment history and permanent upload
receipts and never accesses S3. Stop all writes/recovery and preserve a backup
before Down. Older images reject the newer migration history; a matching database
and remote-object backup is required for restoration. An intentionally deleted
specific object can be absent from an older database backup: this exception needs
an independent remote backup, and Down cannot recreate it. Do not prune tombstones
or run a general orphan/bucket purge. See [deployment recovery](../development/Deployment.md#specific-audio-invalidation-recovery).


## Atomic specific-phonetic edits

`AddMeaningPhoneticEpochs` creates the initially empty `meaning_phonetic_epoch`
table keyed by owned meaning/accent, and adds `meaning_phonetic_epoch` to upload
operations with zero for existing records. No existing ID, phonetic, binding,
receipt, task/plan/history or source revision is rewritten. It performs no network
operation and enables no public editor or AI/TTS worker. Untouched task input
encodings remain compatible; actual edits add the durable epoch to internal task
input versions, while the public audio source revision stays content-based.

`MeaningPhoneticEditService` joins the caller's scoped `IUnitOfWork`; its repository
requires that same context's actual serialized writer transaction. Callers supply
book/word/meaning ownership and per-accent expected source revision, epoch and
**own** asset. An absent input preserves the value; explicit null/blank clears it.
The shared NFC/one-wrapper comparison detects actual changes. Equivalent values
keep their stored representation and create no epoch, invalidation or round.
General meaning replacement cannot overwrite either dedicated phonetic column.

For each real change the same transaction authorizes the precise old published
specific binding before removal, advances an independent positive epoch, writes
the field, releases only its own binding and invalidates both preparation and audio
rounds. Nonblank values get durable pending rounds even when default playback can
fall back; clearing cancels active rounds, retains their history and creates no
specific generation. The caller supplies separate opaque preparation, UK-audio and US-audio versions
from its captured configuration snapshot. A missing version persists a stable
`configuration.missing` marker; later readiness/relevant configuration changes use
the normal round rules. Any failure or cancellation
rolls the entire operation back, including edits to both accents. No commit relies
on a notification, live provider or storage availability.

Reservations capture the epoch internally. Final upload publication re-reads it
in its writer transaction, rejecting old work even after A→B→A with matching
content/own-asset conditions. Background guarded publication additionally checks
the matching current Audio target, round, execution lease owner/fence/expiry and
input in that transaction; the independent upload lease must also still be valid.
A standalone guard check cannot protect a subsequent write. Old successful receipts
remain permanent; a replay never restores an invalidated binding. Deletion claims,
shared references and permanent tombstones retain the preceding queue's safety.

Reuse the full database at `/app/data/vocabulary.db` to retain epochs, waits, work,
cleanup and receipts across restarts/image replacement. New generation and cleanup
are independent: closing generation never restores old audio; closing storage or
failed removal retains deletion work without blocking new publication. Existing
signed URLs/downloads cannot be immediately revoked.

For explicit Down to `20261101185722_AddAudioInvalidations`, stop all writes,
uploads, generation and recovery and preserve a consistent database/object backup.
Down drops the epoch table and captured upload epoch column: it loses ABA protection
history, preserves the edited fields and all other task/cleanup/receipt data, and
cannot recreate deleted remote objects. Do not resume interrupted old executions
across this downgrade. Older images remain rejected by the schema-version gate;
prefer restoring a matching pre-upgrade backup. See [deployment](../development/Deployment.md#specific-phonetic-edit-upgrade).


## Autonomous scheduler persistence

`AddEnrichmentScheduler` adds three initially empty tables without rewriting existing
vocabulary IDs, defaults, specific fields/epochs, assets, bindings, task rounds,
calls, waits or permanent upload receipts:

- `enrichment_discovery` stores independent bounded historical word/meaning cursors and fixed
  cycle upper bounds, so continuous inserts cannot prevent a historical wrap.
  Newest-row and bounded notification lanes cannot advance these cursors.
- `enrichment_service` stores the opaque stable slot/endpoint/provider identity,
  relevant durable configuration version, throttle interval/deadline, consecutive
  service failures, pause interval and a fenced unique request/probe lease.
- `enrichment_service_probe` retains independent started/completed/unknown recovery
  attempts; these never change target dispatch or failure counts.

The service gate is checked immediately before actual dispatch. Short serialized
writer transactions admit or finish a service operation. Throttled/failed item
call outcomes and shared service state commit together or both roll back; a crash
cannot preserve known throttling while consuming it as an unknown item failure. Supplier and storage IO
remain outside them. A lease allows only one request/probe per shared service at
a time, including concurrent UK/US work. Expired probe ownership is recorded as
unknown before a replacement is admitted. Configuration changes fence old results.

Only successful outer business commits publish bounded, best-effort notifications.
Nested saves/transactions do not publish early; rollback discards pending wakeups,
and notifier errors cannot undo a committed approval/import/edit. Notifications are
not the durable queue: bounded scans and specific-edit tasks recover lost wakeups.
The service recovery path never updates round budgets, so a fifth item failure that
also pauses a service stays exhausted after a successful independent probe.

An explicit Down to `20261101194034_AddMeaningPhoneticEpochs` drops these three
tables and loses cursor/throttle/pause/probe history only. It preserves all prior
catalog/audio/task/receipt data and makes no supplier or S3 calls. Stop workers
and writes first and preserve consistent database/configuration/object backups.
The newer-schema startup gate still forbids an older image reading an upgraded
database; restore a matching backup when scheduler history must be retained.

## Administrator enrichment snapshots and retry

The [observation API](../api/Enrichment.md) reads requirements from current explicit
catalog fields/own bindings and joins persisted rounds without calling discovery.
All counts, page rows and independently paged histories share one deferred SQLite
snapshot. No schema migration or new queue is added. Empty specific phonetics do
not create a requirement; same-phonetic playback fallback does not satisfy an own
specific-generation target. Current applicability and historical round state are
reported separately. Service status matches the current internal service identity
and version; those identifiers are not exposed.

Single-step retry reuses the unique `(target_id,retry_key)` round index. Under the
existing serialized writer it first replays an accepted key, otherwise requires
`current_round=expectedRound`, `Failed`, and still-applicable missing work before
creating a successor. Different keys racing for one expected round cannot both
win. Fields retry refreshes Prepare in the same transaction; Prepare retry releases
its still-missing `preparation.failed` dependencies. Successful sides, old failure
history, and unrelated steps survive. Fault/cancellation rolls back every round
and epoch. Existing input/configuration guards remain authoritative at final
publication. These semantics persist in the existing database across restarts;
service recovery alone never resets an exhausted round. Existing consistent
configuration/database/object backup and newer-schema startup policies apply.

Provider-aware preparation adds no migration or new tables. Its configuration
identity includes its own accent's TTS settings when audio is enabled. Legacy
failed preparations with the same catalog/text inputs retain their old round and
allowance until an explicit retry. Single-step retry of locally rejected legacy
Audio plans also refreshes Prepare atomically; transport failures retain it.
Generation accepts bounded optional/alternative dictionary IPA notation while
preserving the original input, persisted envelope and playback source revision.
Successful conditional manual unbinding marks its word for the existing post-commit
notification; rollback clears that mark and notification failure leaves the durable
unbinding intact for cursor discovery. It authorizes no object deletion.
The post-commit mark also carries the exact Audio target (word, optional meaning,
accent). Its bounded notification lane bypasses the word-meaning discovery page
without changing task budgets or adding a durable queue/schema; a lost/overflowed
notification still uses the existing bounded historical discovery.

## Integration acceptance and retained recovery state

The [acceptance map](../development/EnrichmentAcceptance.md) identifies historical
schema comparisons, same-mount container snapshots and actual killed-process
recovery separately. The acceptance changes add no schema. Synthetic container
fixtures retain successful preparation plans, calls, zero-budget throttle waits,
exhausted rounds, cleanup authority/epochs and regeneration work without changing their values;
the private S3 probe separately exercises actual publication before process death.
Keep a matched configuration/database/object backup: exact authorized cleanup can
remove published specific objects, so database retention alone is insufficient.

## Permanent submission review outcomes

`20261102043902_AddMeaningSubmissionReviews` adds nullable `review_comment`,
`reviewer_kind` and `ai_review_verdict` to `meaning_submission`. Existing rows,
IDs, proposal text, result locations and normalized pending-key indexes remain
unchanged; unknown legacy reviewers stay null. New AI revise facts explicitly
exclude those approvals from definition reuse/questions even for POS-only or
phonetic-only changes. Existing manual comparison behavior remains intact.

`meaning_submission_ai_review` stores a permanent ID, unique global attempt ID,
submission ID, verdict, structured AI fields, bounded comment/model, UTC review
milliseconds and original applied flag. Its submission FK is restrictive, with
no catalog FK or cascade. The repository exposes append/read only; the context
rejects tracked changes/deletes of conclusions, and SQLite triggers also reject
UPDATE/DELETE of records (including direct SQL). Checked markers use
separate state described below. The original proposal remains on the retained submission.

`MeaningSubmissionService.ApplyAiReviewAsync` joins the same UnitOfWork writer
and scoped context as manual approvals/rejections. It verifies replay identity,
current pending state and current eligibility before catalog writes. Eligibility
ports must read durable lease ownership/epoch/expiry in that writer. Valid late
conclusions are still appended with `applied=false`. Accept reads current defaults
there and supplies AI phonetics only for blank sides; import/manual merge behavior
is unchanged. Catalog writes, submission metadata and record insertion share one
commit, including rollback after an intermediate catalog SaveChanges.

Down drops the review table and only these three added columns, losing review
history/comments/source and the explicit AI-revise fact. It retains old proposal,
status, result and catalog values; older semantics cannot retain that new fact.
Stop writers and take a consistent pre-upgrade backup before either migration.
Use the existing newer-schema startup gate and matched configuration/database/
object restore policy; an older image cannot open an upgraded database directly.


## Automatic submission review persistence

`20261102052837_AddAutomaticMeaningSubmissionReview` adds only the empty
`meaning_submission_auto_review` table, with restrictive submission FK and a
next-attempt/lease index. No existing row, proposal, permanent conclusion or
pending-key constraint changes. One row holds current attempt/owner/epoch/expiry,
next-attempt time, saturating backoff counter and the latest fixed failure code/time.
A nullable `validated_review_json` temporarily holds only the bounded domain
review fields (not the supplier JSON) so application retry after a crash retains
its exact attempt/model/time without another provider call. It is cleared only
by that attempt's successful completion; a stale owner cannot change newer state.
Permanent result rows remain append-only, including `applied=false` conclusions.

All claims/releases use short serialized writers. Actual eligibility reads the
current lease inside the shared outcome writer/context. Pending rows are the
source of truth even without a scheduling row; failed work has no exhaustion or
manual handover. Failure delays grow 8–120 seconds, honoring longer Retry-After;
review never shares enrichment pauses. See [automatic review](../api/MeaningSubmissions.md#automatic-review-default-off).

Down to `20261102043902_AddMeaningSubmissionReviews` drops only automatic scheduling
and staged application state, preserving submissions, outcomes and catalog rows.
Stop writers and preserve a consistent backup first: staged conclusions would
otherwise be lost and pending work may need another model call after a future
upgrade. Restore database/configuration/root-key/audio objects as one recovery
point; older images cannot bypass the newer-schema gate.


## Independent AI review checks

`20261102063605_AddMeaningSubmissionReviewChecks` adds only the initially empty
`meaning_submission_review_check(record_id PRIMARY KEY, checked_at_unix_ms)` table.
Its restrictive foreign key references the permanent `meaning_submission_ai_review`
record, not the mutable catalog. Existing proposal, conclusion, scheduling and
catalog rows are unchanged. The original conclusion table's no-update/no-delete
triggers remain intact. The check writer inserts once with conflict-ignore and
returns the existing first-check timestamp for every replay; no conclusion update
is needed. It neither publishes enrichment nor invokes an AI client.

Administrator history joins permanent conclusions to retained submitted text and
the optional check fact. Count and page use one deferred, read-only transaction
snapshot with time/ID ordering. Unapplied valid conclusions remain visible.
Vocabulary correction/deletion and later conclusions cannot change earlier checks.
The temporary automatic-review input is not a history source.

Reuse and back up the entire SQLite database in the existing persistent volume.
Before Down, stop writers and back up the database, protected configuration, root
key and matching audio objects consistently. Down to
`20261102052837_AddAutomaticMeaningSubmissionReview` drops only check facts;
permanent conclusions, proposals, automatic retry state and catalog remain.
Restore a matched backup to recover checks. Older images still fail the schema
version gate; do not point one directly at the upgraded database.

## Vocabulary expansion tasks

`AddVocabularyExpansion` adds `Expand` to `enrichment_target`. Such targets require
a meaning ID and null locale; other steps retain their accent requirement. A
filtered unique `(word_id, meaning_id)` index permits one expansion task per sense.
The SQLite rebuild preserves dependent rounds/calls/waits using transactional
backup tables, with foreign keys enabled throughout. Data restoration, deletion
triggers and the migration history record commit atomically; interruption rolls
back the rebuild and permits a subsequent startup to retry. The release-upgrade
test covers every populated application table from `v0.1.8` through this migration.
Input revisions hash spelling, source IDs, POS, meaning, example and expansion
policy version; unrelated audio changes and newly generated relations do not
restart expansion. Successful empty results persist. Successful rounds survive
model/credential rotation; relevant source edits create a new round on discovery.

Validated candidates and captured model/destination are staged in the existing
bounded round plan before publication. The source version and lease are rechecked
inside the serialized catalog transaction. New words, supplement meanings,
`generated` relations, provenance and task completion commit together. Existing
words/relations are preserved. `enrichment_generated_word(word_id, task_id)` marks
newly created words so they receive field/audio enrichment without recursive
expansion; deleting a word cascades its marker. Task histories outlive deletions
as before. See [expansion behavior](../api/VocabularyExpansion.md) and
[upgrade/rollback](../development/Deployment.md#vocabulary-expansion-and-model-driven-activation-upgrade).

## Preparation-pending wait reason

`AddPreparationPendingWaitReason` rebuilds `enrichment_round` and `enrichment_wait`
to add `PreparationPending` to their CHECK constraints. It preserves every round,
call, wait, index and foreign key; dependent call/wait rows are copied to temporary
backup tables and restored in the same transaction that records the migration
version, so an interrupted upgrade rolls back to the previous schema. The Down
migration converts any `PreparationPending` row back to `ConfigurationMissing`
before restoring the old constraints. The reason is written only when a `Fields`
or `Audio` target is waiting for its own `Prepare` target; genuine missing
configuration and disabled storage keep their existing reasons. See
[the API contract](../api/Enrichment.md#read-endpoints).

## AI request batching

`AiRequestBatching` adds three tables without altering existing business rows:
`ai_request_batch`, `ai_request_batch_member`, and `enrichment_service_lease`.
Each physical text request records its kind, opaque configuration version and
member attempt IDs before dispatch. Validated member plans, per-target call
outcomes, adaptive batch size and the shared service outcome commit in one short
transaction. Publication remains independently guarded by the existing catalog
revision and lease. No raw provider envelope or credential is stored.

A busy/locked completion transaction is retried at most twice after rollback,
using the same validated response and member guards. No additional physical
request is sent, and the existing completion marker prevents duplicate accounting.
Successful requests persist a gradually increasing `next_size`, capped at the
initial limit for their kind, so single-item fallback does not become permanent.
These recovery changes require no schema migration or journal-mode change. See
[deployment behavior](../development/Deployment.md#automatic-text-batching-upgrade).

A crash before that transaction leaves an explicitly unconfirmed request; an
expired target/review lease recovers through its existing retry mechanism. A
provider may have processed such a request, so exactly-once remote execution is
not promised. A crash after staging reuses saved plans without another request.
The new service leases enforce bounded TTS concurrency across workers and retain
an in-flight scalar lease from the previous version until completion or expiry.

The dispatch planner reads current rounds into separately bounded recovery, ready,
and text lanes. SQL excludes in-flight words and blocked preparation dependencies,
then deduplicates by word within each lane before limiting recent/historical work.
The single hosted worker reserves words across lanes until preparation and its
selected publication finish; every concurrent job gets a separate scoped context.
These process-local reservations are not durable multi-host locks. Existing target
claims and service leases remain the restart/concurrent-owner boundary. The text
service capacity follows `Enrichment:TextConcurrency` (default 1, range 1–2),
independent of target/TTS concurrency. No schema, migration, journal mode or backup
format changes are introduced by the new planner.
