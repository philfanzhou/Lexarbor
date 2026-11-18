# ADR-012 Learner progress model

- **Status**: proposed
- **Date**: 2026-11-18
- **Depends on**: [ADR-011 PostgreSQL as the only supported storage](./ADR-011-postgresql-only-storage.md)
- **Scope**: how Lexarbor identifies learners, stores per-learner vocabulary state and answer history, schedules reviews, builds study sessions, and deletes a learner's data. Existing catalog, question and submission routes are unchanged

## Context

Lexarbor generates questions on demand but stores nothing about who answered them: [On-demand learning questions](../api/LearningQuestions.md) states that no student ID, history, question batch, answer or schedule is stored. Calling services that teach vocabulary therefore each keep their own learner state, decide what to ask next, and implement their own forgetting curve over Lexarbor's catalog.

The product decision behind [ADR-011](./ADR-011-postgresql-only-storage.md) is to move that logic into Lexarbor. A calling service still authenticates its own users and sends Lexarbor only an identifier; Lexarbor owns the learner's word state, study plans, question selection and spaced-repetition scheduling.

The workload this creates is the one ADR-011 sized PostgreSQL for: one write per answered question, long reads for reports, and a history that cannot be rebuilt.

## Decision

### Learner identity

- A learner is identified by the pair `(caller_id, learner_ref)`. `caller_id` is the caller ID of the [business API key](../api/BusinessApiKeys.md) that made the request; `learner_ref` is an opaque string the calling service chooses (1–128 characters, no whitespace or control characters).
- A caller sees only its own learners. Two calling services that both send `learner_ref = "42"` have two unrelated learners.
- Lexarbor stores no name, email, or other personal attribute of a learner. Calling services should send a pseudonymous reference, not a login name or email address, and keep the mapping themselves.
- Learner routes require a new API key scope, `learning`, separate from `catalog`, so that a key that can read the catalog cannot read or change learner data.

### What is remembered

The unit of memory is the learner and a **meaning** (a word's definition within one book), the same target the question routes take. A learner who studies the same word in two books has two independent states, because the two meanings can differ.

| Table | One row per | Holds |
|---|---|---|
| `learner_plan` | learner and book | enrolled units, daily new and review limits, the learner's day boundary (IANA time zone), paused flag |
| `learner_meaning_state` | learner and meaning | scheduler state: stability, difficulty, due time, last review, repetitions, lapses, phase (`new`, `learning`, `review`, `relearning`), scheduler version |
| `learner_review` | answered question | meaning, question type, mode, correctness, response time, derived rating, the state before and after, scheduler version, answer time, client answer ID |

`learner_meaning_state` is the current state and is updated in place; `learner_review` is append-only and is the source of truth from which the state can be recomputed when the scheduler changes.

### Scheduler

- Use **FSRS** (Free Spaced Repetition Scheduler) with its published default parameters and a target retention of 0.9, implemented as pure code in `Lexarbor.Domain` with no database or host dependency.
- Every state and review row records the scheduler version. A later version may re-derive states by replaying `learner_review`; it never rewrites history.
- An answer becomes an FSRS rating from correctness, response time and question type: a wrong answer is `Again`; a correct answer is `Good`, lowered to `Hard` when slow and raised to `Easy` when fast for that question type. The thresholds are configuration, not contract, and start conservative (`Easy` only for recognition types).
- Per-learner parameter optimization is out of scope here; the review log keeps everything it would need.

### Study sessions

- A calling service asks for the learner's next questions in one book. Lexarbor fills the batch from due reviews first (earliest due first), then new meanings in unit order, within the plan's remaining daily limits for the learner's current day.
- Questions come from the existing question generator, in `review` mode for reviews and `new` mode for new meanings, so question types, distractors and relation questions stay one implementation.
- Lexarbor does not hold a session open. A batch is a suggestion; the state changes only when an answer arrives.

### Recording answers

- An answer names the learner, the meaning, the question type, whether it was correct, the response time, and a client-generated answer ID.
- The review row and the state update are written in one transaction that locks the learner's state row (`SELECT ... FOR UPDATE`). The client answer ID is unique per learner, so a retried request is answered from the stored result instead of counting twice.
- Learner writes do not take the process-wide write lock that catalog writes use today. Removing that lock is [ADR-011](./ADR-011-postgresql-only-storage.md)'s later step, and learner features ship only after it, so that answers from different learners proceed concurrently.

### Review log growth

- `learner_review` is range-partitioned by month on the answer time. Partitions are created ahead by the host's background maintenance, and the schema migration creates the current and next month.
- Retention is a deployment setting. The default keeps everything; a configured retention drops whole monthly partitions older than the limit, never individual rows.

### Catalog changes

- A meaning that is deleted keeps its learner states and reviews, marked unavailable; sessions skip it, and reports can still show history.
- A disabled book's meanings are skipped by sessions until the book is enabled again; states are kept.
- A replaced meaning keeps the same meaning ID and therefore the same learner state. A substantive definition change does not reset memory; that matches how teachers correct definitions.

### Deleting a learner

- A calling service can delete one learner. Lexarbor removes that learner's plans, states and reviews, in every partition, in one transaction, and returns how many rows were removed.
- Revoking an API key does not delete learner data: the caller ID may be issued a new key. Deleting all data of a caller is an administrator operation.

## Alternatives considered

- **Keep learner state in calling services.** Rejected by the product decision: every caller would re-implement scheduling over Lexarbor's catalog, and catalog changes would not reach their states.
- **SM-2 or a fixed Leitner box schedule.** Simpler, but FSRS predicts recall measurably better from the same history, and the append-only log lets Lexarbor change scheduler later without losing data. SM-2 remains a fallback if FSRS's parameters prove unsuitable for short recognition questions.
- **State per word instead of per meaning.** Rejected: question targets, books and distractors are per meaning, and one spelling can carry unrelated meanings in different books.
- **Lexarbor-issued learner IDs.** Rejected: the calling service already has a stable user identity, and a second mapping in each direction adds a lookup without adding protection.
- **Storing answers only as aggregated state.** Rejected: without the review log, scheduler changes, audits and learner deletion reports would all be impossible.

## Consequences

- A new API key scope, `learning`, and new routes under `/api/learners/{learnerRef}/...`. Scopes are stored as one boolean column per scope on `business_api_key`, so the scope adds a column, widens the at-least-one-scope check, and extends key issuance and the administration UI. The exact route and JSON contract is specified with the first implementation issue and documented in `docs/api/`.
- Three new tables, one of them partitioned by month, and a background maintenance task for partitions and retention.
- Learner data is personal data of the calling service's users even when pseudonymous. `SECURITY.md` and the deployment guide gain a section on learner data, deletion, and backups.
- Learner features depend on the removal of the process-wide write lock (ADR-011) and land after it.
- Existing question routes stay stateless and unchanged; calling services migrate to learner routes at their own pace.
