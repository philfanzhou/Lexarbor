# ADR-002 Bundled vocabulary data and user-supplied imports

- **Status**: accepted and implemented; amended on 2026-08-30
- **Date**: 2026-08-05
- **Scope**: Vocabulary data distributed by Lexarbor and the responsibility boundary for data supplied by users. Path and phonetic-field contracts are unchanged

## Context

With an empty database the book list is empty and the question endpoint has nothing to demonstrate. Drawing distractors from within the same book is what separates this service from a general dictionary API: `VocabularyDomainService` selects distractors within a book through `GetRandomByBookExceptAsync(bookId, ...)` and `GetRandomDistinctVocabularyExceptAsync(bookId, ...)`, so question difficulty matches the book's level.

The original decision addressed that experience with a self-authored starter book and also proposed a separately distributed third-party dictionary for optional enrichment. The starter book has been implemented, but the supplemental dictionary table, importer, and release asset have not. Publishing an official dictionary asset would make Lexarbor responsible for selecting a source and maintaining its redistribution licence, attribution, version, and withdrawal lifecycle. Those responsibilities are outside the boundary of a self-hosted vocabulary catalogue and quiz service.

## Decision

Lexarbor distributes one vocabulary dataset: the self-authored `Starter English 300`. It does not select, host, download, endorse, or redistribute third-party dictionary datasets through the source repository, container images, release assets, the startup path, or any other official project channel.

### The starter book stays in the repository and image

`Starter English 300` contains 300 self-authored common words with British phonetics, American phonetics, parts of speech, and Chinese definitions. Its purpose is that a started container immediately has a real book, real questions, and real same-book distractors.

- Its size is negligible, it ships with the repository and image, and it works offline.
- The image tag pins its version, and the startup path stays deterministic.
- On first database creation the initializer writes it in bulk within one transaction. When the database file already exists, startup does not write the seed again.

The sample book ships as `src/Lexarbor.Database/SeedData/starter-vocabulary.tsv` and enters the image as an assembly resource. **No `.db` file is prebuilt into the image.** Startup checks whether the configured database file exists before migrating: an absent file is created, migrated, and seeded; an existing file is migrated only.

This order keeps the writable database on the mounted host volume. A prebuilt database inside the image would either be hidden by the mount or store later writes in the disposable image layer. The existence check must happen before migration because the SQLite provider creates the file while applying migrations.

### User-supplied data stays the user's responsibility

The administration API accepts vocabulary content supplied by the user and stores it in that user's Lexarbor instance. Lexarbor does not claim, inspect, or guarantee the provenance, accuracy, licence status, or downstream usage rights of that content. Users and instance operators are responsible for ensuring that they have the rights required to import, store, use, export, or publish the data they supply.

The current import path remains the per-entry administration operation. Any future bulk-import format or source-neutral conversion tool requires its own decision and implementation issue. Such a feature must accept user-supplied input and must not introduce an official dataset, a source-specific downloader, or an implication that Lexarbor grants rights to the imported content.

No supplemental dictionary table is planned. `vocabulary_meaning` remains the source of question data, and phonetics remain on the shared vocabulary row under the existing API and database contracts.

### Data provenance

The starter book's word selection and arrangement are produced in this repository and taken from no textbook table of contents or existing publication. The words themselves are not protected, but the selection and arrangement of which words belong to a volume can be editorial work and must not be copied from an existing publication for public distribution. The starter book ships with the repository under the MIT License.

User-supplied data is not part of the Lexarbor distribution. The repository's MIT License does not grant rights to that data merely because a user imports it into Lexarbor.

## Alternatives considered

- **Publish a third-party dictionary as an official release asset**: rejected. Keeping the file outside git and the image would reduce repository and image size, but Lexarbor would still be redistributing the data and maintaining its source, licence, attribution, version, and withdrawal lifecycle.
- **Download a dictionary on first startup or on demand**: rejected. It would make Lexarbor select and retrieve an external dataset, introduce a network and source-availability dependency, and blur the responsibility boundary for that data.
- **Commit a dictionary to git or bake it into the image**: rejected. In addition to the same provenance and redistribution responsibilities, it would permanently increase repository history or every image pull.
- **Import a dictionary as one disabled book**: rejected. It would pollute the book list, draw distractors from a general dictionary rather than a level-appropriate book, and repurpose `status=false` from "disabled" to "not a book".

## Consequences

- A new installation still receives the deterministic, MIT-licensed starter book and works offline.
- Lexarbor has no official supplemental dictionary source, table, importer, downloader, or release asset. The earlier unimplemented second-layer plan is withdrawn.
- The supplemental dictionary source decision formerly tracked as PD-001 is resolved by choosing no official source; [Issue #52](https://github.com/philfanzhou/Lexarbor/issues/52) records the change.
- A deployment may contain data supplied by its users, but that data remains outside the Lexarbor distribution. The Lexarbor licence grants no rights to that content; compliance belongs to the user or instance operator.
- This amendment changes documentation only. The `/api/*` and `/admin/*` paths, JSON fields, authentication, rate limiting, SQLite schema, container behaviour, starter seed, and existing administration import remain unchanged.
