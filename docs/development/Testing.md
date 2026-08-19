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
- Mapster 10.0.11 (dependency of the DTO mapping extensions)
- Microsoft.Testing.Extensions.CodeCoverage 18.10.0

## How tests are executed

Tests run on **Microsoft.Testing.Platform** (MTP) rather than the older VSTest — the .NET 10
SDK no longer supports running xUnit v3 under VSTest. The conventions that follow from it:

- The `test.runner` declaration in the root `global.json` makes `dotnet test` go through MTP.
- Both test projects are executables (`<OutputType>Exe</OutputType>` plus
  `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`),
  so the produced exe can also be run directly to execute the tests.
- MTP arguments go after `--`, replacing VSTest's `--logger` and `--collect`:

  ```
  dotnet test Lexarbor.sln --results-directory TestResults -- \
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
| GetWordsAsync | Returns the deduplicated word list for a bookId, sorted by word |

### Database model and migrations

| Scenario | Expectation |
|------|------|
| Meaning to word relationship | Required foreign key, cascade on word deletion |
| Meaning to book relationship | Required foreign key, restrict on book deletion |
| Equivalent meaning constraint | The normalized logical key is unique and in-process concurrent imports stay idempotent |
| First startup | When the database file is absent, migrate and write the 300-word starter book |
| Existing database | Migrate only, and do not write the starter book again |
| Phonetics | The DTO, the model, and the database all keep separate British and American columns |

### HTTP authentication and envelopes

| Scenario | Expectation |
|------|------|
| Anonymous administration request | 401 envelope |
| Ordinary user JWT | 403 envelope |
| Administrator login through fake Identity | Sets an HttpOnly cookie |
| Wrong credentials | 401, no cookie set |
| Identity returns an invalid JWT | 502, no cookie set |
| Administrator cookie or bearer | Can reach the administration endpoints |
| Logout | Responds with an expired cookie, and later administration requests get 401 |
| Identity unreachable or configuration missing | 502 / 503 |
| Cookie administration write without the same-origin header | 403 |
| Public `/api/*` | Does not require an administrator login |
| Unknown route | `/api/*` gives 404; anonymous `/admin/*` gives 401; administrator `/admin/*` gives 404 |
| Unexpected exception | 500 with the generic message, leaking no internal exception |

## How to run

```bash
dotnet test Lexarbor.sln --configuration Release
```

```bash
cd frontend
npm ci
npm run test:types
npx playwright install chromium
npm run test:e2e
```

`npm run test:e2e` first runs a production build, then uses Playwright Chromium to verify administrator session restoration, login, the book list, and the create-book flow. The browser tests intercept the administration API and verify frontend behaviour against fixed responses; real authentication, the HTTP contract, and database behaviour remain the responsibility of the .NET integration tests.

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
