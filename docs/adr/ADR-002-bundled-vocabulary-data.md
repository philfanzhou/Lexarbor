# ADR-002 Layering and distribution of the bundled vocabulary data

- **Status**: accepted; the first layer is implemented, the second is not
- **Date**: 2026-08-05
- **Scope**: Vocabulary's bundled data, dictionary enrichment, and distribution. Path contracts are unchanged; the phonetic field change is covered by ADR-003

## Context

The service currently ships without any vocabulary data. With an empty database the book list is empty and the question endpoint has nothing to demonstrate — and drawing distractors from within the same book is exactly what separates this service from a general dictionary API: `VocabularyDomainService` selects distractors within a book through `GetRandomByBookExceptAsync(bookId, ...)` and `GetRandomDistinctVocabularyExceptAsync(bookId, ...)`, so question difficulty matches the book's level.

Once the service becomes a public repository, "no data after cloning" leaves the most time-consuming step to the user, and the service degrades into a schema plus CRUD. So what data to bundle, and how to distribute it, has to be decided.

Two existing constraints of the data model determine the feasible answers:

- `vocabulary_book` has no unit hierarchy (`src/Lexarbor.Domain/Models/VocabularyBookModel.cs`), and meanings hang directly off a book, so any flat word list maps one-to-one onto a book with no structural impedance;
- `vocabulary_meaning.BookId` is non-nullable with a RESTRICT foreign key (`docs/database/README.md`), so definitions are isolated per book and the same word can carry different definitions and examples in different books.

The second point means a general dictionary's definitions belong to no book and cannot be written into `vocabulary_meaning` directly.

## Decision

Bundled data is split into two layers, distributed separately according to size, licensing origin, and lifecycle.

### Layer one: the sample book (in the repository, in the image)

One complete `Starter English 300` is bundled, holding 300 self-authored common words with British phonetics, American phonetics, parts of speech, and Chinese definitions. Its purpose is that a started container immediately has a real book, real questions, and real same-book distractors.

- its size is at the noise level, it ships with the repository and the image, and it works offline;
- the image tag pins its version, and the startup path stays deterministic;
- on first database creation the initializer writes it in bulk within a single transaction; when the database file already exists nothing is written, so a restart produces no duplicates.

#### The seed ships as a data file, not as a prebuilt database

The sample book ships with the repository as `src/Lexarbor.Database/SeedData/starter-vocabulary.tsv` and enters the image as an assembly resource. **No `.db` file is prebuilt into the image.** At startup the database file at the configured path is checked: if it is absent, the database is created, migrated, and seeded; if it is present, it is migrated only and not seeded.

The reason is that once the `data` directory is mounted, the database file must land on the host volume for later additions to persist with it. A `.db` inside the image produces one of two outcomes: the mount point shadows the file in the image and the seed is invisible, or the writes land in the image layer and are lost with the container.

The existence check must happen before migrating — the SQLite provider creates the file itself while running migrations, so a check made afterwards would always report "already exists".

### Layer two: dictionary enrichment data (a release asset, imported explicitly)

The full dictionary stays out of git, out of the image, and out of the startup path. It is published as a release asset and loaded once by the user through an explicit import entry point.

Its value is not "data on startup" but automatically filling in phonetics, parts of speech, and candidate definitions while importing the user's own word list, leaving a person only to select and rewrite.

### Where the dictionary data lands

| Dictionary field | Destination | Model change |
|----------|------|----------|
| Phonetics | `vocabulary.phonetic_uk`, `vocabulary.phonetic_us` | Both columns hang on the globally unique shared word row, so any later book gets them automatically |
| Part of speech, definition, example | A new dictionary table, used only as a candidate source during import | A new table and an import path |

The dictionary table takes no part in question generation and does not appear in the book list; `vocabulary_meaning` remains the only source of question data.

### Bulk import does not reuse the existing write path

The full dictionary of layer two still needs a dedicated bulk path and does not reuse the per-record domain writes built for administration requests. Layer one is written in bulk directly by the first-run initializer, the database file existence check prevents a restart from writing again, and the SQLite unique index is the structural backstop.

### Data provenance

- **Layer one is self-authored.** The sample book's word selection and arrangement are produced in this repository and taken from no textbook table of contents or existing publication. The words themselves are not protected, but the choice and arrangement of which words belong to which volume is a publisher's editorial work and must not be reused in public distribution. The sample book ships with the code repository under the same licence.
- **Layer two uses an external open-source dictionary**, distributed as a standalone release asset that carries its own licence and attribution. Neither the code repository nor the image contains third-party data.

This provenance strategy keeps layer one independent of the choice made for layer two, so it could be implemented first.

## Alternatives considered

- **Download the full dictionary on first startup**: rejected. It would introduce a network dependency into a migration and seed startup path that is currently deterministic. The failure cases are concrete: offline and intranet deployments simply do not work, pulling a large file across borders is unreliable, the URL can rot, and an interrupted download would need an extra rule about whether the result is an empty database or a blocked startup.
- **Commit the full dictionary to git or bake it into the image**: rejected at the current size. Git is unsuited to large files, and once one enters history it cannot be removed; baking it into the image makes every pull carry that size. If the dictionary eventually chosen is an abridged one in the tens of megabytes, this can be reconsidered, and the gain would be working fully offline.
- **Import the dictionary as one disabled book**: rejected. It would pollute the book list; generating questions from it would mean drawing distractors from the whole dictionary and losing the difficulty match; and `status=false` means the book is disabled, which must not be repurposed to mean this is not a book.

## Consequences

- Layer one is implemented: the repository carries 300 unique seed rows, first-run creation builds the book, the words, and the meanings automatically, and Docker puts the writable database on a persistent volume by default.
- Layer two is still not implemented: neither the dictionary table, nor the bulk import entry point, nor the release asset exists.
- The provenance strategy is settled (see above). Choosing a specific dictionary and verifying its redistribution licence is not done; see PD-016 in `docs/pending-decisions.md`. That blocks layer two only, not the layer one sample book.
- The layering also isolates licensing risk: the dictionary ships as a replaceable standalone asset, the code repository's history contains no third-party data, and replacing or withdrawing a data source affects neither the code repository nor existing clones.
- The `/api/*` and `/admin/*` paths are unchanged; the word DTO's single phonetic field has already been replaced by `phoneticUk` and `phoneticUs` per ADR-003.
