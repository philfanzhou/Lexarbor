# ADR-013 Plan scopes follow the catalog

- **Status**: accepted
- **Date**: 2026-11-22
- **Depends on**: [ADR-012 Learner progress and historical continuity](./ADR-012-learner-progress-model.md)
- **Scope**: How a daily plan's new words are chosen over time, what is frozen, and what follows catalog changes. Public HTTP routes and JSON fields are unchanged

## Context

A plan stores its scope as books and optional units in `vocab_plan.segments`, not as a list of words. When a learning day is first prepared, Lexarbor reads the scope's entries from the catalog in catalog order. It merges the learner's ready notebook items and drops meanings the learner already has memory cards for. It then freezes that day's selection in `vocab_plan_day.daily_tasks`.

Administrators edit books while plans are in progress. They can add words, remove meanings and change the order of entries. Until now, the effect of these edits on in-progress plans was implicit in the code.

## Decision

1. **The catalog is the only source of a plan's words.** A plan keeps its scope and does not copy the words that the scope contained when the plan started.
2. **A day is frozen when it is first prepared.** Catalog changes apply to days prepared later and never to a day that is already frozen. If a frozen target can no longer produce a question, it is skipped, and its open slot is refilled from the current scope.
3. **Added entries become learnable on the next prepared day, in catalog order.** Removed meanings are no longer selected. Cards already learned remain and are reviewed at their saved location ([Review locations](../api/LearnerProgress.md#review-locations)).
4. **Plan progress follows the current scope.** The plan's total word count is recomputed from the current scope when the scope runs out of new words.
5. **Notebook items keep the meaning the learner chose.** An item stores the catalog IDs of that meaning. It does not follow the scope of any plan, and a removed meaning no longer produces questions.
6. **A plan's history is its change log and its frozen days.** `vocab_plan_change` records scope and setting changes, and each day's roster records what was assigned. Lexarbor does not record what the scope contained on a past day.

## Consequences

- Catalog corrections reach learners in progress without a migration or a plan change.
- Editing a book that plans use changes the remaining work and its order for those learners from their next prepared day. Treat such edits as visible to learners.
- Reports about what was assigned read the frozen days. The question of what the scope contained when a plan started cannot be answered.
- Locking a plan to its starting word list would require a new decision. It needs a per-plan snapshot of entries and a rule for whether catalog corrections reach those entries. This is not supported.
