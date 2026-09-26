# Vocabulary database

Vocabulary supports SQLite only. The default database file is `data/vocabulary.db`, and the EF Core model together with the migrations under `src/Lexarbor.Database/Migrations` describes the complete schema. The service has not shipped, so no legacy PostgreSQL migration chain is kept.

## Core relationships

```text
vocabulary_book (1) <-[RESTRICT]- vocabulary_meaning -[CASCADE]-> (1) vocabulary
```

| Table | Responsibility | Key constraints |
|----|------|----------|
| `vocabulary` | Words with British and American phonetics | `word` is unique; `normalized_word` is generated and indexed; `phonetic_uk` and `phonetic_us` are nullable |
| `vocabulary_book` | Book metadata and enabled state | `status=false` means disabled |
| `vocabulary_meaning` | A word's meaning within one book | Both foreign keys are required; the normalized logical key is unique |

Deleting a word clears its meanings through `ON DELETE CASCADE`. Deleting a book uses `ON DELETE RESTRICT`; when related meanings exist the business layer answers 409 and the administrator should disable the book instead.

The database logical key for an equivalent meaning is:

```text
(vocabulary_id, book_id, lower(trim(coalesce(part_of_speech, ''))), trim(meaning))
```

The last two components are persisted as SQLite stored generated columns and carry a unique index, so duplicate data cannot be produced by going around the application layer.

## First-run creation

The connection string defaults to `Data Source=data/vocabulary.db`. The startup order is fixed:

1. create the parent directory and run the SQLite migrations;
2. switch the database to write-ahead logging.

Startup writes no books, words, or meanings, whether the file is new or already exists, so a new database starts empty and data already in an existing one is neither overwritten, duplicated, nor removed. Lexarbor ships no vocabulary data ([ADR-002](../adr/ADR-002-bundled-vocabulary-data.md)); databases created by releases that still loaded the former `Starter English 300` book keep it unchanged.

The database file must live on a persistent volume. Docker mounts the host's `data/` at `/app/data` by default; never put a prebuilt `.db` into the image.

## Write consistency

- A new meaning must reference a book that exists and is enabled.
- When an update carries a word, book, or meaning ID and that object does not exist, the answer is 404; it must never become an insert.
- Updating a meaning must confirm the meaning belongs to the current word and book.
- The normalized value of a word is `word.Trim().ToLowerInvariant()`. The database keeps the same expression as the generated column `normalized_word` (`lower(trim(word))`), and the lookup that resolves an import to an existing word compares against it, so the comparison is an index seek rather than a scan of the table. The column is virtual rather than stored, because SQLite refuses `ALTER TABLE ... ADD COLUMN` for a stored generated column and the column has to be addable to a database that already exists; the index holds the computed value either way. It carries no unique constraint: asserting one would fail on a database that already holds two spellings of one word.
- Re-importing the same word, book, normalized part of speech, and definition is idempotent.
- A SQLite deployment is single-instance only; a process-level write transaction lock serializes administrative writes, and the database's unique index is the last line of defence.
- `UnitOfWork` maps SQLite constraint errors to 409 and never exposes the internal error to the client.

## Scoped cleanup

The new cleanup commands are separate from the legacy book DELETE, which still refuses a book with meanings. `VocabularyCleanupService` validates the current book, ownership, selected memberships and optional exact name within the same UnitOfWork write transaction as deletion. Preview instead uses a deferred read transaction without the process write semaphore.

Let R be the meanings selected by book and action, and A the distinct word IDs in R. One shared parameterized predicate defines R in preview counts and commit. Commit records A in a connection-local temporary table, deletes R, deletes only words in A with `NOT EXISTS` any remaining meaning, then optionally deletes the book. Other/disabled books' references and unrelated historical orphan words remain untouched. No cascade is used to widen the scope, no whole book of meaning entities is loaded, and there is no new business table or migration. The temporary table is dropped in `finally`; tracked entries are cleared after bulk SQL so later operations cannot reuse deleted entities. Failure at any stage rolls back all business rows.

A single instance remains the supported deployment. SQLite serializes external writers; busy/constraint failures keep existing 503/409 mappings. Preview counts can become stale between requests. These destructive commands offer no undo, replay safety or automatic orphan sweep.
