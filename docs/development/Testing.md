# Unit testing conventions

This document describes the structure, coverage, and conventions of the `Lexarbor` service's unit test projects.

## Test project structure

| Project | Path | Coverage |
|------|------|---------|
| `Lexarbor.Domain.Tests` | `tests/Lexarbor.Domain.Tests/` | Domain layer services |
| `Lexarbor.Service.Tests` | `tests/Lexarbor.Service.Tests/` | DTO conversion, exception middleware, authentication, and HTTP integration |

## Test framework and dependencies

- xUnit.net v3 4.0.0 (assertions always use xUnit's built-in `Assert.*`; no third-party assertion library)
- Moq 4.20.72
- Microsoft.AspNetCore.Mvc.Testing 10.0.11 (WebApplicationFactory integration tests)
- Microsoft.EntityFrameworkCore.Sqlite 10.0.11 (both domain and HTTP tests run against real SQLite)
- Mapster 10.0.12 (dependency of the DTO mapping extensions)
- Microsoft.Testing.Extensions.CodeCoverage 18.10.0

## How tests are executed

Tests run on **Microsoft.Testing.Platform** (MTP) rather than the older VSTest — the .NET 10
SDK no longer supports running xUnit v3 under VSTest. The conventions that follow from it:

- The `test.runner` declaration in the root `global.json` makes `dotnet test` go through MTP.
  Under MTP the solution must be passed with `--solution`; some .NET 10 SDKs reject it as a
  positional argument (`Specifying a solution for 'dotnet test' should be via '--solution'.`).
- Both test projects are executables (`<OutputType>Exe</OutputType>` plus
  `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`),
  so the produced exe can also be run directly to execute the tests.
- MTP arguments go after `--`, replacing VSTest's `--logger` and `--collect`:

  ```
  dotnet test --solution Lexarbor.sln --results-directory TestResults -- \
    --report-xunit-trx --coverage --coverage-output-format cobertura
  ```

  Do not specify a report file name: both test projects write into the same directory, so a
  fixed name would have them overwrite each other, while the default name carries a run
  identifier and avoids the collision.
- The xUnit1051 analyzer in xUnit v3 requires asynchronous calls to be passed
  `TestContext.Current.CancellationToken`, so that tests respond to cancellation and timeouts.

## Service.Tests coverage

### DTO mapping extension scenarios (Mapster DTO ↔ Model)

| Scenario | Expectation |
|------|------|
| VocabularyDto → VocabularyModel | Fields map correctly |
| VocabularyModel → VocabularyDto | Fields map correctly |
| VocabularyMeaningDto → VocabularyMeaningModel | Fields map correctly |
| VocabularyMeaningModel → VocabularyMeaningDto | Fields map correctly |
| VocabularyBookDto → VocabularyBookModel | Fields map correctly |
| VocabularyBookModel → VocabularyBookDto | Fields map correctly |

## Domain.Tests coverage

### VocabularyDomainService

| Scenario | Expectation |
|------|------|
| GetDetailAsync succeeds | Returns (word, meanings) |
| SearchAsync paging | Returns the correct page |
| Word normalization | Trims surrounding whitespace and lowercases |
| Repeated import | Reuses the word and the equivalent meaning |
| Book missing or disabled | Returns NotFound or a business rule error respectively |
| Update with a non-existent ID | Returns NotFound and creates no new object |
| Meaning ownership mismatch | Returns Conflict |
| Question generation | Distractors come only from the same book, deduplicated by word |
| Too few question candidates | Returns BusinessRuleException, which HTTP maps to 422 |
| Batch import into an empty book, then resubmitted | Creates every entry, then reuses every entry; the counts say so |
| Equivalent entries within one batch | Store one word and one meaning |
| Later entries in a batch | Overwrite earlier phonetics and examples; blank values never clear a stored one |
| Batch that fails while writing an entry | Rolls back the entries written before it |
| Batch with an invalid shape, a missing book, or a disabled book | Rejected before any write |
| Two identical batches imported concurrently | Serialized: one creates every entry, the other reuses them |

### VocabularyBookDomainService

| Scenario | Expectation |
|------|------|
| GetAllAsync | The public list returns enabled books only |
| GetByCategoryAsync with grade | Filters correctly |
| SearchAsync paging | The administration list includes disabled books and the query runs on the database side |
| AddOrUpdateAsync | Creation is correct; updating a non-existent ID returns NotFound |
| DeleteAsync | An empty book can be deleted; a book in use returns Conflict |
| GetAllCategoriesAsync | Returns the deduplicated category list |
| GetAllEducationLevelsAsync | Returns the deduplicated education level list |
| GetAllGradesAsync | Returns the deduplicated grade list |
| GetGradesByEducationLevelAsync | Filters grades by education level |
| GetWordsAsync | Returns one page of a book's distinct words, sorted by word, with the total word count |

### Database model and migrations

| Scenario | Expectation |
|------|------|
| Meaning to word relationship | Required foreign key, cascade on word deletion |
| Meaning to book relationship | Required foreign key, restrict on book deletion |
| Equivalent meaning constraint | The normalized logical key is unique and in-process concurrent imports stay idempotent |
| First startup | When the database file is absent, migrate and leave every business table empty |
| Existing database | Migrate only, leaving existing books, words, and meanings unchanged |
| Distribution | The database assembly embeds no vocabulary data resource |
| Phonetics | The DTO, the model, and the database all keep separate British and American columns |

### HTTP authentication and envelopes

| Scenario | Expectation |
|------|------|
| Anonymous administration request | 401 envelope |
| Ordinary user JWT | 403 envelope |
| Administrator login through fake Identity | Sets an HttpOnly cookie |
| Wrong credentials | 401, no cookie set |
| OIDC provider refuses the client (`invalid_client`, `invalid_scope`, …) | 502, no cookie set |
| SignaCore-shaped token with per-application audience and no scope requested | Login succeeds; a shared-audience token gives 502 |
| Identity returns an invalid JWT | 502, no cookie set |
| Administrator cookie or bearer | Can reach the administration endpoints |
| Logout | Responds with an expired cookie, and later administration requests get 401 |
| Identity unreachable or configuration missing | 502 / 503 |
| Cookie administration write without the same-origin header | 403 |
| Public `/api/*` | Does not require an administrator login |
| Unknown route | `/api/*` gives 404; anonymous `/admin/*` gives 401; administrator `/admin/*` gives 404 |
| Unexpected exception | 500 with the generic message, leaking no internal exception |
| Batch import, every row of the ADR-005 model | The documented status, envelope, and `errors`, with the database unchanged on every rejection |
| Batch import body over 1 MiB | 413 envelope on a Kestrel-hosted factory, with `Content-Length` and chunked; TestServer does not enforce the limit |

### Rate limiting

| Scenario | Expectation |
|------|------|
| Login beyond the permit limit | 429 envelope carrying `Retry-After` |
| One address exhausts the login limit | Another address is still admitted |
| Public `/api/*` beyond the permit limit | 429, unknown `/api/*` routes included |
| Administration endpoints | Never rate limited |
| `X-Forwarded-For` with no trusted proxy | Ignored, so a caller cannot mint its own partition |
| `X-Forwarded-For` from a trusted proxy | Partitions on the real client address |
| A policy disabled by configuration | Every request is admitted |
| A permit limit or window below 1 | Startup fails rather than the limit being clamped or dropped |

## How to run

```bash
dotnet test --solution Lexarbor.sln --configuration Release
```

```bash
cd frontend
npm ci
npm run test:types
npx playwright install chromium
npm run test:e2e
```

`npm run test:e2e` first runs a production build, then uses Playwright Chromium to verify administrator session restoration, login, the book list, the create-book flow, and the word import flow. The import specification covers the request payload shape, the blank optional fields being omitted rather than sent empty, the form reset, client-side validation, and the status-to-message mapping — including that a status the mapping does not know falls through to the server's message and that a `success:false` envelope inside a 200 is still reported as a failure. The batch import specification covers the full `{ bookId, entries }` payload in source order with trimmed columns and blank optional columns omitted; a byte-order mark, and CRLF and lone CR line endings read from a file; invalid lines reported by their physical line number; no request being sent without a book, without data lines, with an invalid line, with more than 500 lines, with a payload over 1 MiB in UTF-8 bytes, or from a file over 1 MiB; server `errors` shown on the source lines their `index` points to, and a malformed `errors` list ignored; the per-status messages, including a network failure; the 401 redirect; and one request however often the button is clicked. The batch import formats specification covers a CSV file with a byte-order mark, CRLF endings, a reordered header, a blank trailing column, and quoted fields holding commas, doubled quotes, and line breaks producing the same full `{ bookId, entries }` payload as the same batch in TSV, and the same from pasted text with CSV selected; the selector switching to CSV for a `.csv` file; CSV positions being the line a record starts on, with blank records counted and `#` records read as data; a record with the wrong field count marked invalid; every header and quoting error refusing the whole input without a request, including a semicolon-separated file; a header with no data rows; GBK bytes in a `.csv` or `.tsv` file refused with the text area and format left unchanged, and UTF-8 files with and without a byte-order mark accepted; an unsupported extension and an oversized `.csv` refused before reading; and server `errors` mapped to CSV start lines and cleared when the text, the format, or the book changes. The batch import JSON specification covers `.json` files with and without a byte-order mark, and pasted text with JSON selected, producing the same full payload as the same batch in TSV, including surrounding whitespace and optional fields written as empty strings, as `null`, or left out; the selector switching to JSON and the position column reading 序号; a top-level object, a top-level string, a `{ bookId, entries }` payload, and a syntax error with its position each refusing the whole input without a request; unknown fields including `__proto__`, values that are not strings shown as they were written, items that are not objects, and a missing `word` or `meaning` each marking only their item invalid by its number; an empty array having no data rows; server `errors` mapped to item numbers; and a non-UTF-8 `.json` file and a `.jsonl` file refused. The browser tests intercept the administration API and verify frontend behaviour against fixed responses; real authentication, the HTTP contract, and database behaviour remain the responsibility of the .NET integration tests.

GitHub Actions additionally collects TRX and Cobertura coverage, runs the container health check and persistence tests, and keeps the Playwright trace, screenshots, and video on failure. See [Repository automation](./Automation.md) for the full description.

## Conventions

- DTO mapping extension tests verify field mapping through the public `ToEntity()` and `ToDto()` extension methods.
- Domain layer tests use the real DomainService, real repositories, and a SQLite in-memory or temporary file database.
- DTO mapping extension tests require `InternalsVisibleTo`, which is configured in the Service project's `.csproj`.
- Assertions always use xUnit's built-in `Assert.*`; no third-party assertion library such as
  FluentAssertions is introduced. This is the same rule as the test framework section above,
  restated here as applying to every test project.
- Do not modify the code under test to suit a test; when testability needs to improve, update this document first and change the code afterwards.
- Identity is doubled by a fake HTTP handler implementing the full contract; JWTs use a test signing key and depend on no real administrator password.
- When real Identity credentials are unavailable, a fake Identity result must not be described as a successful real integration.
