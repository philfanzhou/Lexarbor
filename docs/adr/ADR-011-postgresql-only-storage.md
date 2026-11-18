# ADR-011 PostgreSQL as the only supported storage

- **Status**: accepted
- **Date**: 2026-11-17 (proposed), 2026-11-18 (accepted)
- **Supersedes**: [ADR-003 SQLite as the only supported storage](./ADR-003-sqlite-only-storage.md)
- **Scope**: Lexarbor's database provider, startup database gate, upgrade path from SQLite, write concurrency strategy, migration history, deployment shape, and backups. Public HTTP routes and JSON fields are unchanged

## Context

[ADR-003](./ADR-003-sqlite-only-storage.md) moved Lexarbor from PostgreSQL to SQLite so that a self-hosted installation would need no external database. It accepted the loss of horizontal scale-out because the service was a read-mostly vocabulary catalog whose data "can be rebuilt from a backup or an import".

That premise no longer holds. Lexarbor is going to own per-learner vocabulary progress: a calling service identifies a learner by an opaque learner id (Lexarbor still does not authenticate learners), and Lexarbor keeps each learner's word state, study plans, question selection, and spaced-repetition scheduling. Keeping that logic inside Lexarbor rather than in calling services is a product decision. It changes the workload in four ways that matter to storage:

1. **Writes become the hot path.** Every answered question writes a review record and updates the learner's word state. Writes scale with active learners, not with administrators.
2. **Reads and writes overlap.** Learner reports, plan generation, and administrator statistics are long reads over the same tables that answer submissions write.
3. **Data is no longer rebuildable.** Learning history cannot be re-imported. Losing it is losing the product's value for that learner.
4. **Tables become large.** A review log grows by roughly one row per answered question, without an upper bound.

### What SQLite does today

- [`UnitOfWork`](../../src/Lexarbor.Database/Repositories/UnitOfWork.cs) serializes every write transaction in the process through one `SemaphoreSlim`, and about 66 call sites rely on the resulting single-writer guarantee (`IsWriteInProgress`, `OwnsWriteContext`, `ExecuteAdmittedTransactionAsync`).
- The database runs under the rollback journal (see [Deployment](../development/Deployment.md#rollback-journal)). A committing write needs every reader to finish first, and the configured busy timeout is 5 seconds.
- Many schema changes are applied by EF Core on SQLite as a full table rebuild.
- Backups are file copies taken with writes stopped, or an online-backup tool.

### Evidence

A local measurement on a developer laptop (Python `sqlite3`, a 317 MB database with 3 million learner-word rows and 5 million review rows, one transaction per answer that inserts a review row and updates a word-state row) gave:

| Journal mode | Answers per second | One answer while a report query (`GROUP BY` over the review log) is running |
|---|---|---|
| Rollback journal, `synchronous=FULL` (current) | ~2,000 | waited **7,306 ms** |
| WAL, `synchronous=FULL` | ~7,100 | waited 2 ms |
| WAL, `synchronous=NORMAL` | ~10,000 | — |

These figures exclude EF Core and the process-wide semaphore, and laptop `fsync` is optimistic, so the absolute numbers are an upper bound. They still show the shape of the problem: raw write throughput is not the bottleneck at expected sizes (10,000 learners with 20% online answering every 6 seconds is about 330 answers per second), while **reads blocking writes past the 5-second timeout** is. Under the current configuration a single report query would make answer submissions fail.

## Decision

Support PostgreSQL only, reached through two releases:

1. **The transition release** runs on PostgreSQL. On startup it copies an existing SQLite database into PostgreSQL, verifies the copy, and only then starts serving. SQLite is read once, by the copy, and never serves requests.
2. **The PostgreSQL-only release** removes all SQLite code. It refuses to start on an installation that still needs the copy.

At no point does Lexarbor serve requests from both providers, and no release keeps a dual-provider capability.

### Provider and startup

- Use `Npgsql.EntityFrameworkCore.PostgreSQL` for EF Core.
- Use `ServiceMantle.Database.PostgreSql` for the startup database gate: its bootstrap provider, target preparation, deployment capability, and migration lock provider replace the SQLite equivalents registered in [`LexarborDatabaseStartup`](../../src/Lexarbor.Database/LexarborDatabaseStartup.cs). Per [ADR-008](./ADR-008-servicemantle-first-and-hosted-login.md), Lexarbor does not re-implement what ServiceMantle provides.
- `ConnectionStrings:Default` becomes a required PostgreSQL connection string. There is no default. A missing value, an invalid value, or a value that is still a SQLite connection string (`Data Source=...`) fails startup with a fixed diagnostic that never echoes the value.
- The supported server is PostgreSQL 16 or later.
- Start a new PostgreSQL baseline migration. The SQLite migration history is not carried into PostgreSQL.

### Matching SQLite semantics

The copy must not change what the data means or how the API orders it:

- **Normalized columns.** The generated columns in [`VocabularyDbContext`](../../src/Lexarbor.Database/VocabularyDbContext.cs) use `lower(trim(...))` and back unique indexes. SQLite's `lower()` folds ASCII letters only, while PostgreSQL's folds every Unicode letter, so two rows that are distinct in SQLite (`CAFÉ` and `Café`) could collide in PostgreSQL. The PostgreSQL columns fold `A`–`Z` only, for example through `translate()`, so the normalized values are byte-identical to SQLite's.
- **Collation.** SQLite compares text bytewise, and administrator and public queries page by `Word`. The PostgreSQL database uses the `"C"` collation so that ordering and pagination are unchanged.
- **Identity sequences.** Rows are copied with their original keys. After the copy, every identity sequence is advanced past the largest copied key.
- **Types.** SQLite stores timestamps, booleans, and GUIDs as text or integers. The copy converts each value through the EF model's types, and verification compares the converted values, not the stored text.

### Transition release: startup copy

Before the startup gate reports ready, the transition release decides from two facts: whether PostgreSQL holds a completed-copy marker, and whether the SQLite source file exists. The source path defaults to `data/vocabulary.db` and can be configured.

| PostgreSQL marker | SQLite file | Action |
|---|---|---|
| present | any | Serve from PostgreSQL. The SQLite file is not opened. |
| absent | present | Copy, verify, and mark, then serve from PostgreSQL. |
| absent | absent | New installation. Create an empty PostgreSQL schema and serve. |

The copy proceeds as follows:

1. Acquire the ServiceMantle migration lock, so that only one process copies.
2. Apply the release's bundled SQLite migrations to the source, so that it reaches the last SQLite schema, then open it read-only.
3. In **one PostgreSQL transaction**:
   1. create the schema;
   2. copy every table in foreign-key order, without generated columns;
   3. advance the identity sequences;
   4. verify, per table, the row count and an ordered per-row hash computed over primary-key order from the converted values, including the generated columns that PostgreSQL recomputed;
   5. write the completed-copy marker, recording the source schema version, the row counts, and the verification result;
   6. commit.
4. Leave the SQLite file in place, unmodified, as the rollback snapshot.

Any failure, including a verification mismatch, rolls back the transaction. PostgreSQL stays empty, the SQLite file stays untouched, startup fails with a fixed diagnostic code, as the startup gate does today, and the next start retries from the beginning. Verification and the switch are atomic: an installation either has a verified copy and serves from PostgreSQL, or has no copy at all.

While copying, `/health/live` reports alive and `/health/ready` reports not ready. The copy has its own time budget, separate from the 30-second gate budget, and the deployment guide tells operators to allow for it in container health checks.

Data Protection key envelopes, API key hashes, and administrator sessions are copied as they are. The keys do not change, so existing sessions stay valid.

### PostgreSQL-only release

- Removes the SQLite provider, the copy, the bundled SQLite migrations, and every SQLite reference.
- Refuses to start when PostgreSQL has no completed-copy marker and the configured data directory still contains `vocabulary.db`. The diagnostic tells the operator to upgrade through the transition release first, so that skipping the transition never looks like data loss.
- Keeps reading the marker only for that check. The marker table itself stays part of the schema.

### Write concurrency

- The transition release keeps the process-wide write semaphore, so that only the provider changes in that step.
- A later step removes it. Every invariant that today depends on serialized writes gets an explicit database-level guarantee: unique constraints and foreign keys as the last line of defence, `SELECT ... FOR UPDATE` for read-modify-write sequences, and transaction-scoped advisory locks (`pg_advisory_xact_lock`) where no row exists yet to lock, such as equivalent-meaning creation.
- Unique-violation and serialization failures are mapped to the existing `ConflictException` contract.
- Code that depends on SQLite's implicit `rowid` order gets an explicit, indexed ordering column.

### Testing

Domain and HTTP tests run against a real PostgreSQL server (a disposable container per test run). No in-memory or SQLite substitute is used, preserving ADR-003's rule that tests and production share one implementation. The transition release additionally tests the copy against SQLite fixture databases, including non-ASCII uppercase words, words that sort differently under a locale collation, and an injected verification failure.

### Deployment

- The Lexarbor image no longer contains a database. A deployment is the Lexarbor container plus a PostgreSQL server. The repository ships a Compose example that runs both, with the database on a named volume.
- `/app/data` keeps the Data Protection root key file when the key is not injected. In the transition release it also holds the SQLite source until the operator removes it.
- Backups move from file copies to `pg_dump`, with continuous archiving (point-in-time recovery) recommended for deployments that hold learner data.

### Learner data model

This ADR decides storage only. The learner data model (learner identity, word state, review log, study plans, scheduling algorithm, retention, and deletion of a learner's data) is a separate ADR. That ADR should plan for a time-partitioned review log so that retention can drop partitions instead of deleting rows.

## Alternatives considered

- **Switch connections at runtime.** Serve from SQLite while copying in the background, then move connections to PostgreSQL. Rejected: writes made during the copy would either be lost or need change capture, the verification could not be atomic with the switch, and the release would serve from both providers.
- **A separate offline import tool.** Rejected in favour of the startup copy: every self-hosted operator would have to run an extra command correctly, while the startup copy needs only a new connection string.
- **Stay on SQLite and switch back to WAL.** It removes reads blocking writes, which is the immediate failure. It was rejected because it leaves the other constraints in place: one instance, so every upgrade is downtime for learners; table-rebuild migrations on tables that will hold hundreds of millions of rows; and file-level backups for data that cannot be rebuilt. The project has also already left WAL once (see [Deployment](../development/Deployment.md#rollback-journal)).
- **SQLite by default, PostgreSQL optional.** Rejected for the reasons in ADR-003: the tested path and the deployed path would differ, and migrations, queries, and conflict semantics would be maintained twice.
- **PostgreSQL for learner data only, SQLite for the catalog.** Rejected. Question selection joins learner state with the catalog, and two stores would still mean two providers, two backup procedures, and no transaction spanning both.
- **Keep learner logic in calling services.** Rejected by the product decision above. Each caller would re-implement scheduling and question selection over Lexarbor's catalog.

## Consequences

- **Self-hosting needs a PostgreSQL server.** This reverses the main benefit of ADR-003 and is a breaking deployment change. Release notes and the upgrade guide must lead with it.
- **Operators must not skip the transition release.** The PostgreSQL-only release refuses to start instead of starting empty.
- **The first transition-release start takes longer.** The service is not ready until the copy finishes. Later schema changes can use PostgreSQL's online operations (`ADD COLUMN` without a rewrite, `CREATE INDEX CONCURRENTLY`).
- **Rollback is limited.** Until PostgreSQL has accepted writes, an operator can roll back to the previous release with the untouched SQLite file. After that, rolling back to SQLite loses every write made on PostgreSQL. The upgrade guide tells operators to back up the SQLite file and the Data Protection root key before upgrading.
- **ADR-003 is superseded** when this ADR is accepted. The storage and container persistence directory rules in `AGENTS.md` and `CONTRIBUTING.md` are updated in the same change.
- **Horizontal scale-out becomes possible but is not promised by this ADR.** Storage stops being the blocker. Running more than one instance still requires a review of in-process state, such as per-process rate-limit counters and background scheduler ownership, which is separate work.
- **Public HTTP routes, JSON fields, ordering, authentication, administrator roles, business API keys, and rate-limit configuration are unchanged.**
- **Documentation changes:** [Database facts](../database/README.md), [Deployment](../development/Deployment.md), [Testing](../development/Testing.md), the README quick start, the container test script, and the release notes of both releases.
