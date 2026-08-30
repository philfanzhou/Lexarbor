# ADR-003 SQLite as the only supported storage

- **Status**: accepted and implemented
- **Date**: 2026-08-05
- **Scope**: Vocabulary's database provider, migrations, concurrent write strategy, and phonetic fields. Paths are unchanged; the word DTO's phonetic fields are a contract change

## Context

Before this change the service used PostgreSQL. The connection string was composed from Consul's shared configuration by `SharedPostgreSqlConnectionStringFactory` in `the former monorepo's shared Consul component`, and table repair relied on the database helpers in `the former monorepo's shared common component`. Those dependencies suited deployment inside the platform, but they are a burden for distributing the service as a public repository: a user must first provide a PostgreSQL instance and understand the shared configuration conventions before anything starts.

The service has not shipped (see ADR-001) and there is no existing data to migrate, so the cost of changing provider is at its lowest right now.

The code already had a two-way split on `_context.Database.IsRelational()` (`src/Lexarbor.Database/Repositories/VocabularyRepositories.cs`), but the non-relational side of that branch served the in-memory provider used by tests, and **SQLite satisfies `IsRelational()` as well**, so it would fall into the PostgreSQL branch. The following places were PostgreSQL-specific:

| Location | Provider-specific feature |
|------|----------|
| The distractor queries in `VocabularyRepositories.cs` | `DISTINCT ON`, `btrim` |
| `AcquireEquivalentMeaningLockAsync` in `VocabularyRepositories.cs` | `pg_advisory_xact_lock` |
| `DatabaseInitializer.cs` | `CHECK` / `FOREIGN KEY ... NOT VALID` |
| `UnitOfWork.cs` | `PostgresException` and `PostgresErrorCodes` exception mapping |

## Decision

Support SQLite only, remove the PostgreSQL provider, and keep no dual-provider capability.

- The connection string degrades to a local file path and no longer depends on Consul, Common, or a shared connection string factory.
- The `IsRelational()` split is deleted and every code path uses one provider; tests and production run the same database implementation.
- Migrations are regenerated. The service has not shipped, so the existing PostgreSQL migration history is not kept.

### Concurrent writes

`pg_advisory_xact_lock` existed to stop multiple instances from concurrently inserting an equivalent meaning. A SQLite deployment is single-instance, so the application serializes write transactions with a process-level `SemaphoreSlim`, which makes a second equivalent request re-query after the first commits and reuse the record. The database also stores the normalized part of speech and definition in two stored generated columns and carries a unique index over the full logical key as the last line of defence.

### The historical-data compatibility code is removed with it

Tightening constraints progressively through `NOT VALID` was designed for existing dirty data in PostgreSQL. A brand-new SQLite database has none, so the old migrations, diagnostics, and repair code are all removed and constraints are created in their strong form directly.

### Phonetics split into British and American columns

The single `vocabulary.phonetic` column is split into `phonetic_uk` and `phonetic_us`, both nullable. The corresponding JSON fields are `phoneticUk` and `phoneticUs`, and the administration import page collects them separately. Carrying both is the normal arrangement for Chinese learners. The change lands in the same migration rebuild as the provider change, so it costs no extra migration.

Callers importing user-supplied data must map a phonetic explicitly to the British or American field and must not rely on the service to guess a dialect. The administration contract exposes both fields separately.

## Alternatives considered

- **SQLite by default with PostgreSQL optional**: rejected. Developing daily on PostgreSQL while distributing SQLite to users means the tested path and the distributed path differ. The role claim defect recorded in ADR-001 is exactly that shape: a test double implemented a contract the real implementation did not, and the defect stayed invisible for a long time. Two providers would also mean maintaining migrations, queries, and conflict semantics twice indefinitely, and would keep the public repository carrying `the former monorepo's shared common component` and its Consul assumptions.
- **Stay on PostgreSQL**: rejected. A user must supply a database instance before starting, which conflicts with the goal of public distribution.

## Consequences

- **Horizontal scale-out is lost.** SQLite has a single writer, so the deployment must be single-instance with a persistent volume mounted. Given that this service is read-mostly and its data can be rebuilt from the starter book or an import, that is judged acceptable.
- **Backups change** from the platform's shared PostgreSQL arrangement to file-level backups.
- The code and the Dockerfile no longer reference `the former monorepo's shared common component` or `the former monorepo's shared Consul component`. The Identity address is supplied through ordinary configuration and environment variables.
- ADR-002 was later amended to withdraw the unimplemented official supplemental dictionary plan. Lexarbor does not attach or distribute a prebuilt third-party dictionary database; its writable database is still created at runtime at the configured path so that it lands on the host's mounted volume.
- The public and administration paths are unchanged; requests and responses involving a word replace the old `phonetic` with `phoneticUk` and `phoneticUs`, which is a deliberate public contract change.
- Both the domain and HTTP tests run against real SQLite; first-run creation, seed count, skipping the seed for an existing file, and concurrent idempotence all have automated coverage.
