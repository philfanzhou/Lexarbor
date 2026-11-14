# ADR-009: Meaning submissions and review

- **Status:** Accepted; automatic-review decision amended 2026-11-01, implemented 2026-11-02.
- **Scope:** Integration vocabulary proposals, one supplementary book, manual review and explicitly enabled automatic AI review.

## Context

An external learning system needs to propose meanings missing from every enabled
book while Lexarbor remains responsible for shared vocabulary and pronunciation.
Lexarbor stores no learner identity or learning history. Unreviewed proposals
must not become public catalog meanings.

## Decision

1. Keep immutable proposed word/POS/meaning/example in `meaning_submission` outside
   every book. Equivalent pending proposals reuse one ID; unchanged approvals can
   be reused. Rejections and explicit AI revisions can be proposed again.
2. Use the single supplementary book configured by `MeaningSubmissions:BookId`.
   Approval uses ordinary catalog matching in the same transaction as the outcome.
   Pending questions use proposal text; unchanged approvals use their catalog
   meaning; rejected or revised proposals cannot create questions. Existing
   supplementary-book distractor fallback remains unchanged.
3. Preserve manual administrator review, optional rejection/revision comments and
   independent on-demand AI advice. Business API keys with the `meaning-submissions` scope cannot
   review; administrators use the existing Cookie+CSRF or administrator Bearer
   boundary. Public vocabulary reads remain unchanged.
4. **Allow automatic AI decisions only behind the default-off**
   `MeaningSubmissions:Ai:AutoReviewEnabled` switch in the shared online settings.
   This supersedes the original advice-only decision. The maintainer explicitly
   accepted the risk that an incorrect AI decision can reach the public catalog
   without human confirmation. Administrators choose whether to enable it and use
   existing vocabulary editing to correct mistakes; there is no bulk rollback.
5. Only integration pending proposals are reviewed, including all old/disabled-period
   backlog. Imports and administrator catalog edits never trigger AI review.
   Submission commits and responses contain no model call. An independent bounded
   worker discovers durable pending rows, claims expiring leases and invokes only
   the configured review service outside database writers.
6. Accept preserves original word/POS/meaning/example and fills only blank current
   default IPA. Revise can change POS/meaning/IPA, preserves word/example and always
   marks `definitionKept=false`; reject preserves the original proposal with a reason.
   Misspellings must be rejected with an intended-spelling suggestion, never silently
   rewritten. Model data cannot execute tools or extend the allowed fields.
7. Persist each validated conclusion with its actual captured model/time and original
   application fact. Same-attempt replay is idempotent; valid late/losing results are
   retained as unapplied. Permanent conclusions are immutable and independent of
   later catalog edits/deletion. Separate human checked facts must not overwrite them.
8. Failures remain pending and retry forever with capped exponential delay and longer
   valid Retry-After. Disabling, missing configuration and shutdown pause safely;
   restart and configuration repair recover without manual handover. Review has no
   shared enrichment service pause or exhausted-failure threshold. Unknown supplier
   answers do not create conclusions; strict exactly-once remote billing is not promised.
9. Send only the four proposal fields. Enforce a 64 KiB/depth-8 response, strict field
   presence/type/length and one cancellation budget. Keep keys, raw answers, provider
   errors and review text out of logs/telemetry. Store only bounded validated fields
   and fixed safe recovery codes in SQLite. No learner identity is added.

## Alternatives

- Per-learner books expose learning lists and create fragmented small dictionaries;
  rejected in favor of one shared supplementary book.
- Writing pending proposals directly to a book exposes unreviewed content;
  rejected in favor of separate proposals and atomic approval.
- Depending on an identity-provider round trip for integration service calls adds
  availability coupling; retain the local public-key ring and short-lived tokens.
- The original rejection of automatic AI decisions is **superseded** by the
  maintainer's explicit default-off risk decision. Manual advice remains available.

## Consequences

The [API contract](../api/MeaningSubmissions.md) defines comments, immutable results,
strict automatic decisions and safe administrator status. The
[deployment guide](../development/Deployment.md) and
[database contract](../database/README.md) describe independent retry state,
consistent backups and explicit migration Down. Keep `/app/data`, configuration,
root key and private audio objects consistent. The schema gate refuses an older
image against a newer database. Down loses the removed feature's state; restoring
a matching pre-upgrade backup is the full recovery path. AI correctness, fixed
completion time and universal compatible-model support are not guaranteed.
