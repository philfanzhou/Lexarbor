# Pending decisions

## PD-003 Learner progress model

[ADR-012](./adr/ADR-012-learner-progress-model.md) proposes how Lexarbor identifies learners, stores their per-meaning state and answer history, schedules reviews with FSRS, and deletes a learner's data. Learner features are not implemented until it is accepted.

## Resolved

- PD-001, the former supplemental dictionary decision, was resolved by amending [ADR-002](./adr/ADR-002-bundled-vocabulary-data.md) to withdraw official third-party dictionary distribution.
- PD-002, PostgreSQL as the only supported storage, was resolved by accepting [ADR-011](./adr/ADR-011-postgresql-only-storage.md), which supersedes [ADR-003](./adr/ADR-003-sqlite-only-storage.md).
