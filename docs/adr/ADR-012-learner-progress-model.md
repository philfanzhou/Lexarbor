# ADR-012 Learner progress and historical continuity

**Accepted, 2026-11-19.** Depends on [PostgreSQL storage](ADR-011-postgresql-only-storage.md).

Calling services already hold years of learning history. Reconstructing a learner from the current catalog or replaying historical ratings would change due dates, erase retired meanings and lose information that earlier versions never recorded.

## Decision

A trusted backend authenticates with a business key carrying the `learning` scope. Its stable caller ID and the learner's UUID together identify a learner. The backend authorizes the person and the requested student or teacher action. Each caller's PostgreSQL schema contains its complete learning state. Key rotation under the same caller ID preserves access.

The memory card key is the learner, normalized word and normalized meaning. The same pair can be learned across books. Plans retain their book and unit scope, teacher ownership, change history and daily frozen work. Questions retain their issued prompts, options, expected answers, expiry, linked questions and attempts. A retry of an answered question returns its recorded result. The original transaction advances the card and appends the review. FSRS-6 uses the stored phase, steps, stability, difficulty and elapsed time. Incorrect answers yield Again, slow correct answers Hard, and other correct answers Good. The default target retention is 0.9. Historical missing state stays null.

The same model serves free reviews, reports, check-ins and notebooks, including submission revisions and word-family sources. Retired plan evidence and original rows imported from historical backups can coexist with current state. An explicit learner deletion removes both. Normal learning has no scheduled history expiry.

The storage contract supports gradual adoption with one writer per learner. A caller can provision new learners over HTTP. A migrating backend also binds its routing state to the destination store UUID and rotates a generation when it transfers ownership. This permits verified transfers and fail-closed recovery after an interrupted handoff.

## Consequences

Learner schemas are part of the database backup and recovery unit. The original answer, card and daily state travel together. A caller can retain its learner IDs, UI, authentication and in-flight questions. The existing catalog question generator continues to work independently. See [Learner progress API](../api/LearnerProgress.md).
