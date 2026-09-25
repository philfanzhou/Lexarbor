# ADR-002 Bundled vocabulary data and user-supplied imports

- **Status**: accepted and implemented; amended on 2026-08-30 and 2026-09-25
- **Date**: 2026-08-05
- **Scope**: Vocabulary data distributed by Lexarbor and the responsibility boundary for data supplied by users. Path and phonetic-field contracts are unchanged

## Context

With an empty database the book list is empty and the question endpoint has nothing to demonstrate until an administrator creates a book and adds its words. Drawing distractors from within the same book is what separates this service from a general dictionary API: `VocabularyDomainService` selects distractors within a book through `GetRandomByBookExceptAsync(bookId, ...)` and `GetRandomDistinctVocabularyExceptAsync(bookId, ...)`, so question difficulty matches the book's level.

The original decision addressed that experience with a self-authored starter book and also proposed a separately distributed third-party dictionary for optional enrichment. The starter book was implemented, but the supplemental dictionary table, importer, and release asset were not. Publishing an official dictionary asset would make Lexarbor responsible for selecting a source and maintaining its redistribution licence, attribution, version, and withdrawal lifecycle. Those responsibilities are outside the boundary of a self-hosted vocabulary catalogue and quiz service.

The same reasoning applies to the self-authored starter book. Even a list written in this repository obliges the project to keep demonstrating that its word selection and arrangement come from no existing publication, and it invites downstream users to question the provenance of data they did not choose. The 2026-09-25 amendment removes it ([Issue #71](https://github.com/philfanzhou/Lexarbor/issues/71)).

## Decision

Lexarbor distributes no vocabulary data. It does not ship, select, host, download, endorse, or redistribute any vocabulary dataset, whether self-authored or third-party, through the source repository, container images, release assets, the startup path, or any other official project channel. Every book and entry in an instance is created or imported by its administrators.

### A new instance starts empty

**No `.db` file is prebuilt into the image**, and startup writes no books, words, or meanings. Startup creates and migrates an absent database file and migrates an existing one; in both cases the business tables are left exactly as they were.

This order keeps the writable database on the mounted host volume. A prebuilt database inside the image would either be hidden by the mount or store later writes in the disposable image layer.

### User-supplied data stays the user's responsibility

The administration API accepts vocabulary content supplied by the user and stores it in that user's Lexarbor instance. Lexarbor does not claim, inspect, or guarantee the provenance, accuracy, licence status, or downstream usage rights of that content. Users and instance operators are responsible for ensuring that they have the rights required to import, store, use, export, or publish the data they supply.

The current import path remains the per-entry administration operation. Any future bulk-import format or source-neutral conversion tool requires its own decision and implementation issue. Such a feature must accept user-supplied input and must not introduce an official dataset, a source-specific downloader, or an implication that Lexarbor grants rights to the imported content.

No supplemental dictionary table is planned. `vocabulary_meaning` remains the source of question data, and phonetics remain on the shared vocabulary row under the existing API and database contracts.

### Data provenance

The Lexarbor distribution contains no vocabulary data, so it makes no provenance claim about any. Every book and entry in an instance is user-supplied data.

User-supplied data is not part of the Lexarbor distribution. The repository's MIT License does not grant rights to that data merely because a user imports it into Lexarbor.

### The former starter book

Until the 2026-09-25 amendment, Lexarbor shipped `Starter English 300`: 300 self-authored common words with British and American phonetics, parts of speech, and Chinese definitions, embedded in the database assembly as `SeedData/starter-vocabulary.tsv` and written in one transaction when a database file was first created. Its purpose was that a started container immediately had a real book, real questions, and real same-book distractors. Its word selection and arrangement were produced in this repository, and it was distributed under the MIT License.

The resource and the seeding code have been removed. Databases that already contain the book keep it unchanged: startup never deletes rows, because the book may be in use or may have been edited by its operator.

## Alternatives considered

- **Publish a third-party dictionary as an official release asset**: rejected. Keeping the file outside git and the image would reduce repository and image size, but Lexarbor would still be redistributing the data and maintaining its source, licence, attribution, version, and withdrawal lifecycle.
- **Download a dictionary on first startup or on demand**: rejected. It would make Lexarbor select and retrieve an external dataset, introduce a network and source-availability dependency, and blur the responsibility boundary for that data.
- **Commit a dictionary to git or bake it into the image**: rejected. In addition to the same provenance and redistribution responsibilities, it would permanently increase repository history or every image pull.
- **Keep the starter book behind a configuration switch that is off by default**: rejected. The data would still ship in the repository, the image, and every release, so the distribution would still carry it.
- **Delete the starter book from existing databases through a migration or at startup**: rejected. It would silently remove data an operator may be using or may have edited.
- **Import a dictionary as one disabled book**: rejected. It would pollute the book list, draw distractors from a general dictionary rather than a level-appropriate book, and repurpose `status=false` from "disabled" to "not a book".

## Consequences

- A new installation starts with an empty catalogue. The public endpoints return empty results until an administrator creates a book and adds words, and the service still works offline.
- Existing databases keep any `Starter English 300` rows they already contain; startup neither adds to nor removes them.
- Lexarbor has no official supplemental dictionary source, table, importer, downloader, or release asset. The earlier unimplemented second-layer plan is withdrawn.
- The supplemental dictionary source decision formerly tracked as PD-001 is resolved by choosing no official source; [Issue #52](https://github.com/philfanzhou/Lexarbor/issues/52) records the change.
- A deployment may contain data supplied by its users, but that data remains outside the Lexarbor distribution. The Lexarbor licence grants no rights to that content; compliance belongs to the user or instance operator.
- The 2026-08-30 amendment changed documentation only.
- The 2026-09-25 amendment removes the starter seed from new databases. The `/api/*` and `/admin/*` paths, JSON fields, authentication, rate limiting, SQLite schema, migrations, persistent data directory, and existing administration import remain unchanged.
