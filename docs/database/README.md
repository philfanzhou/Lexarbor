# Vocabulary database

Vocabulary supports SQLite only. The default database file is `data/vocabulary.db`, and the EF Core model together with a single `InitialCreate` migration describes the complete schema. The service has not shipped, so no legacy PostgreSQL migration chain is kept.

## Core relationships

```text
vocabulary_book (1) <-[RESTRICT]- vocabulary_meaning -[CASCADE]-> (1) vocabulary
```

| Table | Responsibility | Key constraints |
|----|------|----------|
| `vocabulary` | Words with British and American phonetics | `word` is unique; `phonetic_uk` and `phonetic_us` are nullable |
| `vocabulary_book` | Book metadata and enabled state | `status=false` means disabled |
| `vocabulary_meaning` | A word's meaning within one book | Both foreign keys are required; the normalized logical key is unique |

Deleting a word clears its meanings through `ON DELETE CASCADE`. Deleting a book uses `ON DELETE RESTRICT`; when related meanings exist the business layer answers 409 and the administrator should disable the book instead.

The database logical key for an equivalent meaning is:

```text
(vocabulary_id, book_id, lower(trim(coalesce(part_of_speech, ''))), trim(meaning))
```

The last two components are persisted as SQLite stored generated columns and carry a unique index, so duplicate data cannot be produced by going around the application layer.

## First-run creation and the starter book

The connection string defaults to `Data Source=data/vocabulary.db`. The startup order is fixed:

1. before migrating, check whether the configured data file exists;
2. create the parent directory and run the SQLite migrations;
3. only when the file did not previously exist, read the assembly's embedded `SeedData/starter-vocabulary.tsv`;
4. write `Starter English 300`, its 300 unique words, and their meanings in a single transaction.

Every starter entry carries a British phonetic, an American phonetic, a part of speech, and a Chinese definition. An existing file is migrated only and the seed is never reloaded, so data a user adds later is neither overwritten nor duplicated by the startup logic.

The database file must live on a persistent volume. Docker mounts the host's `data/` at `/app/data` by default; never put a prebuilt `.db` into the image.

## Write consistency

- A new meaning must reference a book that exists and is enabled.
- When an update carries a word, book, or meaning ID and that object does not exist, the answer is 404; it must never become an insert.
- Updating a meaning must confirm the meaning belongs to the current word and book.
- The normalized value of a word is `word.Trim().ToLowerInvariant()`.
- Re-importing the same word, book, normalized part of speech, and definition is idempotent.
- A SQLite deployment is single-instance only; a process-level write transaction lock serializes administrative writes, and the database's unique index is the last line of defence.
- `UnitOfWork` maps SQLite constraint errors to 409 and never exposes the internal error to the client.
