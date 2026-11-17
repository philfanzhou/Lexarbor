# Pending decisions

## PD-002 PostgreSQL as the only supported storage

[ADR-011](./adr/ADR-011-postgresql-only-storage.md) proposes replacing SQLite with PostgreSQL so that Lexarbor can own per-learner vocabulary progress. Until it is accepted, [ADR-003](./adr/ADR-003-sqlite-only-storage.md) remains in force and SQLite is still the only supported storage.

## Resolved

The former supplemental dictionary decision, PD-001, was resolved by amending [ADR-002](./adr/ADR-002-bundled-vocabulary-data.md) to withdraw official third-party dictionary distribution.
