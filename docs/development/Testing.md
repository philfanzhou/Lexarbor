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

## Build identity verification

`ApplicationVersion.Read(Assembly)` is an internal assembly reader exposed to the service
test assembly through `InternalsVisibleTo`. Tests construct assemblies with missing,
blank, malformed, or duplicate attributes to exercise the same reader used by the Host.
The immutable runtime snapshot always selects the Host assembly, never the test runner.

Run the real publish/startup matrix separately (Python 3, .NET 10, and free port 5008):

```bash
python3 .github/scripts/test-build-identity.py
```

Each of the six rows (release, prerelease, two different edge revisions, defaults, and a missing version attribute)
uses a separate temporary output and intermediate directory. The script reads the actual
published Host assembly and its runtime snapshot, then starts that artifact outside the
repository with conflicting runtime environment/configuration values. It checks the exact
startup identity, anonymous health envelope, and authorized version HTTP fields/no-store against those exact build inputs. A .NET SDK/BCL fixture binds only a temporary loopback port and publishes OIDC discovery/JWKS; ephemeral RSA keys and the synthetic token stay in process memory/pipes and never enter logs. Production Bearer validation remains unchanged. No `.git` is present in the publish output.
Failures preserve temporary artifacts for diagnosis; successful runs remove them. Cancellation
stops the test process and never changes a running deployment.

The container script accepts `IMAGE [VERSION [REVISION [CHANNEL]]]`; pass `unknown` to
assert a missing revision. CI supplies a synthetic version, full SHA, and `release`, and
checks them in real container logs while retaining health, non-root, and persistence checks.

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
| Word normalization | Trims surrounding whitespace; equivalence is the normalized key, while the stored display spelling keeps the imported casing |
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
| Login beyond the permit limit | 429 Problem Details with fixed title/code, correlation id and positive `Retry-After` |
| One address exhausts the login limit | Another address is still admitted |
| Public `/api/*` beyond the permit limit | 429, unknown `/api/*` routes included |
| Administration endpoints | Never rate limited |
| `X-Forwarded-For` with no trusted proxy | Ignored, so a caller cannot mint its own partition |
| `X-Forwarded-For` from a trusted proxy | Partitions on the real client address |
| A policy disabled by configuration | Every request is admitted |
| Enabled permits outside 1..10000 or seconds outside 10..600 | Startup fails rather than the limit being clamped or dropped |

Additional rate-limit regressions cover numeric boundary acceptance, disabled
invalid values and policy independence, default quotas reaching the shared startup
snapshot after late test-host configuration, separate policy/address buckets,
IPv4-mapped and unknown-address buckets, concurrent admission without queueing,
and shared rejection-write cancellation. The header adapter preserves an existing
shared `Retry-After` and touches only product-policy 429 responses. Existing
trusted-proxy, hosted-start route-equivalence and six-security-header guards remain.
Browser coverage reads a 429 Problem Details title and keeps the current session
and page, without a login/forbidden redirect.

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

`npm run test:e2e` first runs a production build, then uses Playwright Chromium to verify administrator session restoration, login, the book list, the create-book flow, and the word import flow. The book list is checked for its two empty states (no book yet, and a search that matched nothing), one request for the first page when the page size changes, and a failed load shown in the page with a retry. The import specification covers the request payload shape, the blank optional fields being omitted rather than sent empty, the form reset, client-side validation, and the status-to-message mapping — including that a status the mapping does not know falls through to the server's message and that a `success:false` envelope inside a 200 is still reported as a failure; it also covers the confirmation naming the imported word until the form is edited, and a retry after the book list fails to load. The batch import specification covers the full `{ bookId, entries }` payload in source order with trimmed columns and blank optional columns omitted; a byte-order mark, and CRLF and lone CR line endings read from a file; invalid lines reported by their physical line number; no request being sent without a book, without data lines, with an invalid line, with more than 500 lines, with a payload over 1 MiB in UTF-8 bytes, or from a file over 1 MiB; server `errors` shown on the source lines their `index` points to, and a malformed `errors` list ignored; the per-status messages, including a network failure; the 401 redirect; and one request however often the button is clicked. The batch import formats specification covers a CSV file with a byte-order mark, CRLF endings, a reordered header, a blank trailing column, and quoted fields holding commas, doubled quotes, and line breaks producing the same full `{ bookId, entries }` payload as the same batch in TSV, and the same from pasted text with CSV selected; the selector switching to CSV for a `.csv` file; CSV positions being the line a record starts on, with blank records counted and `#` records read as data; a record with the wrong field count marked invalid; every header and quoting error refusing the whole input without a request, including a semicolon-separated file; a header with no data rows; GBK bytes in a `.csv` or `.tsv` file refused with the text area and format left unchanged, and UTF-8 files with and without a byte-order mark accepted; an unsupported extension and an oversized `.csv` refused before reading; and server `errors` mapped to CSV start lines and cleared when the text, the format, or the book changes. The batch import JSON specification covers `.json` files with and without a byte-order mark, and pasted text with JSON selected, producing the same full payload as the same batch in TSV, including surrounding whitespace and optional fields written as empty strings, as `null`, or left out; the selector switching to JSON and the position column reading 序号; a top-level object, a top-level string, a `{ bookId, entries }` payload, and a syntax error with its position each refusing the whole input without a request; unknown fields including `__proto__`, values that are not strings shown as they were written, items that are not objects, and a missing `word` or `meaning` each marking only their item invalid by its number; an empty array having no data rows; server `errors` mapped to item numbers; and a non-UTF-8 `.json` file and a `.jsonl` file refused. The batch import Excel specification builds its workbooks in the test with `fflate`, so no binary fixture is committed. It covers a workbook with leading and middle blank rows, a reordered header, shared and inline strings, numbers, booleans, dates with and without a time, formulas with cached values, a merged cell, an error value, and a second sheet producing the same full payload as the same batch in TSV, with sheet row numbers as positions and a notice naming the sheet read; a missing `meaning` column, an unknown header, and a value under a blank header each refusing the whole workbook, a blank column beyond the header ignored, and a header alone having no data rows; a workbook declaring more than 32 MiB, and the same workbook with its central directory size forged, both refused with the page still usable; bytes that are not a zip, an `.xls` file, and an oversized `.xlsx` refused; the text area and format disabled while a workbook is loaded, and removing it or choosing a text file returning to text input; server `errors` mapped to sheet rows and cleared when the file or the book changes; the worker chunk requested only once an `.xlsx` file is chosen; and, with a reader that never answers and a fake clock, the 15-second timeout. The layout specification runs axe with the WCAG 2.1 A and AA tags at 1440 px and 768 px over the whole login and forbidden pages, over the header and navigation of `/books`, `/import`, and `/import/batch`, and over the whole of `/books` with books, with none, and with the new book dialog open, of `/import` on arrival and after a successful import, and of `/import/batch` when empty, with valid and invalid rows, and with a file-level error, each with no violation allowed; it also covers no sideways scrolling on any of the five routes at 768 px or on `/import/batch` with a preview, the batch import help following the selected format and sitting beside the text area at 1440 px and below it at 768 px, the empty preview's text, the navigation collapsing to named icons, each navigation link reaching its page with only the current one carrying `aria-current="page"`, Tab reaching the logout button, the build-version button, and then the four links with a visible focus ring, logout posting to `/admin/auth/logout` and returning to the login page even when it answers 500 (with the error shown), and Element Plus's Chinese texts in the pagination and the delete confirmation. The version specification covers the administration header's build identity: every channel's label (`v1.2.3`, a preserved prerelease, `edge · ` with a seven-character SHA or 提交未知, 开发版本, and 版本未知) with the full version, complete revision, and channel behind a dialog that opens from the keyboard and from a pointer click, closes on Escape and an outside click, and returns focus to its button; one request per session generation with no refetch on route changes, a refetch after a reload, no duplicate request when the guard restores a freshly created session, and no request on the guest pages; a 500, a malformed success body, and a network failure each reading 版本未知 with the page still working and no retry; a current 401 and 403 keeping their redirects; delayed late answers across a logout and a same-username re-login, both a late success that cannot paint the new session and a late 401 that cannot clear it; nothing written to localStorage or sessionStorage; and a 375 px header holding a long prerelease and a long username without sideways scrolling or hiding the logout button. The vocabulary specification covers the two administration list pages and their shared read-only detail drawer: entering a book's word list from 教材管理 with the book's name, status, and whole-book deduplicated-word and meaning counts (a word with several meanings stays one row), server-executed keyword and paging parameters with the whole-book counts unchanged by the keyword, the empty-book versus keyword-miss distinction, disabled books being maintained the same way with a clear way back, a missing book reported as such rather than as an empty list, and a failed load with a working retry; the whole-library list showing disabled-only and unassigned words with their membership tags; the book filter reading the paged administration book search remotely, paging past its first page to a disabled option, sending `bookId` to the word search, and offering a read-only retry of its own; the drawer opened from both lists showing the shared fields once and meanings grouped per book with status, closing on Escape with focus returned to its opening button, and separating a failed load, a 404, and an unassigned word; out-of-order answers for a replaced word and a replaced book, a same-document book switch, an answer landing after the page was left, and a 401 keeping the shared redirect; the drawer's two independent edit groups, namely the shared replacement confirming with every referencing book named (disabled included) before sending all three fields with a blank phonetic as an explicit null, a meaning replacement scoped to its book/word/meaning path with the cleared example sent as null and the other book's meanings untouched, cancelling the confirmation or the form sending nothing, blank required fields refused client-side without a request, a 400 keeping the draft with the server reason, a 409 offering an explicit reload that replaces the draft, a 404 reporting the target gone while the draft stays, an aborted answer re-reading the detail once without replaying the save, switching targets dropping the draft and a late save answer staying out of the new target, repeated clicks during a meaning save sending exactly one request, an unassigned word editing through the shared area only, and hostile word and meaning text rendered as text and never as HTML; and axe with the WCAG 2.1 A and AA tags plus sideways-scroll checks at 1440 px and 768 px for both pages and the open drawer, including the drawer with both edit groups open. The cleanup specification covers the shared preview-and-confirm flow and every entry into it: clearing a book through a preview that shows the target name, all three counts, the estimate note, and the shared-retention note, with cancellation committing nothing; deleting a legacy book with the exact-name requirement gating the confirm and `confirmedBookName` sent on commit; a rename between preview and commit refused with a fresh preview offered; the current page's checkbox selection sent as exactly its word ids with the selection dropped on reload and the toolbar disabled when empty; a single-row removal; a page emptied by a removal falling back to the last valid page; a removed book returning to the book list; one meaning deleted from the drawer through its own book, word, and meaning ids with the detail re-read; one meaning first edited as a full replacement and then deleted through the same drawer, with the edited content visible, the deletion request carrying the same three ids, and both the detail and the list refreshing after each commit; the last meaning's deletion closing the drawer of the word that went with it; an aborted commit warning about the unknown outcome, re-querying the page, and never being replayed; a double click committing exactly once; distinct preview refusals for gone, stale, and busy; and axe with the WCAG 2.1 A and AA tags plus sideways-scroll checks at 1440 px and 768 px for the delete dialog with a long book name, the word list with a selection, and the meaning-delete dialog. The hosted-auth specification covers the SignaCore login mode the backend selects: a hosted method read rendering only the SignaCore navigation and never posting a password, a password method read keeping the credential form, a method read answered with a 500, an unknown mode value, or a network failure each offering no sign-in action until a retry succeeds, the single start navigation with the allowlisted `returnUrl` (and the allowlist dropping an unusable redirect), each of the six fixed `reason` values reading as its own notice with an unknown one staying harmless, the provider round trip returning to the original page while keeping password, code, and token material out of every request and out of both storages with one version request, a canceled and a non-administrator sign-in landing back on the login page without a session, a reload restoring the hosted session, an expired session returning through the provider, a logout navigating the browser to the prepared `logoutUrl`, a hosted local-only logout saying the provider may still hold a session, and a password-mode logout staying silent. The browser tests intercept the administration API and verify frontend behaviour against fixed responses; real authentication, the HTTP contract, and database behaviour remain the responsibility of the .NET integration tests.
GitHub Actions additionally collects TRX and Cobertura coverage, runs the container health check and persistence tests, and keeps the Playwright trace, screenshots, and video on failure. See [Repository automation](./Automation.md) for the full description.

## Hosted login live integration check

The browser specifications verify the administration frontend against fixed responses, and the .NET suites verify the backend against a mocked provider. The end-to-end proof that both halves speak to a real SignaCore is a manual loopback run. It needs both source trees, .NET 10, and Node.js, and keeps every credential synthetic and out of both repositories.

1. Build and start an isolated SignaCore instance. SQLite keeps it self-contained, the Development `Database`-section fallback skips the bootstrap file, and a dedicated port keeps it apart from any SignaCore already running locally:

   ```bash
   dotnet build /path/to/SignaCore/src/SignaCore.Host -c Release
   mkdir -p /tmp/lexarbor-it && cd /tmp/lexarbor-it
   ASPNETCORE_ENVIRONMENT=Development \
   Database__Provider=SQLite \
   Database__ConnectionString="Data Source=/tmp/lexarbor-it/signacore.db" \
   Endpoints__Http=5011 \
   dotnet /path/to/SignaCore/artifacts/bin/SignaCore.Host/release/SignaCore.Host.dll
   ```

2. Read the one-time setup code from its console banner and complete first-run setup through the API; `allowNonHttpsIssuer` is the documented opt-in the HTTP loopback issuer needs:

   ```bash
   curl -X POST http://127.0.0.1:5011/management/v1/setup \
     -H 'Content-Type: application/json' -H 'X-ServiceMantle-Request: 1' \
     -d '{"code":"<setup-code>","input":{"publicBaseUrl":"http://127.0.0.1:5011","allowNonHttpsIssuer":true,"jwtAudience":"SignaCore.Services","username":"<admin>","password":"<admin-password>"}}'
   ```

   The host stops itself after the submission; start the same command again to run the normal host.

3. Sign in with `POST /api/admin/session/bearer/login` (same request header) and register the application with the returned Bearer token, in the order SignaCore's hosted-login guide requires: `POST /api/admin/apps` with `clientType:"Confidential"` (keep the one-time secret), `PUT /api/admin/apps/<appId>/audience-mode` with `PerApplication`, `POST /api/admin/apps/<appId>/oidc/redirect-uris` twice — kind `Redirect` for `http://127.0.0.1:5008/admin/auth/callback` and kind `PostLogout` for `http://127.0.0.1:5008/admin/auth/logout/return` — then `PUT /api/admin/apps/<appId>/oidc-policy` with `allowAuthorizationCode:true`, `allowedScopes:["openid","profile"]`, and `allowRefreshToken:false`. Create one non-administrator account through `POST /api/admin/users`; only the bootstrap administrator receives `role=admin` in the access token, which is exactly the denial case.

4. Build the administration frontend into the Host's web root and start Lexarbor in Development on its fixed port 5008, pointing the `OidcCode` section and the identity trust settings at the isolated instance:

   ```bash
   cd frontend && npm ci && npm run build
   cp -R dist ../src/Lexarbor.Host/wwwroot
   cd ../src/Lexarbor.Host
   ASPNETCORE_ENVIRONMENT=Development \
   AdminAuthentication__Provider=OidcCode \
   AdminAuthentication__OidcCode__ClientId=<appId> \
   AdminAuthentication__OidcCode__ClientSecret=<secret> \
   AdminAuthentication__OidcCode__RedirectUri=http://127.0.0.1:5008/admin/auth/callback \
   AdminAuthentication__OidcCode__PostLogoutRedirectUri=http://127.0.0.1:5008/admin/auth/logout/return \
   IdentityService__Authority=http://127.0.0.1:5011 \
   IdentityService__Issuer=http://127.0.0.1:5011 \
   IdentityService__Audience=<appId> \
   dotnet bin/Release/net10.0/Lexarbor.Host.dll
   ```

   Remove the copied `wwwroot` afterwards and rebuild: it is a build artifact,
   not source; a present `wwwroot` turns the SPA fallback into a 200 answer for
   otherwise unmatched routes in tests that run against this content root, and a
   build whose static-web-asset manifest recorded the directory fails host
   startup once it is deleted without a rebuild.

5. Drive `http://127.0.0.1:5008/#/login` in a browser (or a throwaway Playwright script) and verify the acceptance matrix against the live pair: the hosted button replaces the password form; the administrator signs in on the provider's own login page and lands back on the stored return route with the session header; localStorage and sessionStorage stay empty and no request carries a password, authorization code, token, or secret; logout navigates through the provider's `/oauth2/logout` and returns to `/#/login?reason=logged_out`; the next sign-in asks for credentials again, proving the upstream session really ended; a non-administrator lands on `/#/login?reason=denied`; and Cancel on a fresh provider page lands on `/#/login?reason=canceled`. A denied user keeps its live provider session, so SignaCore immediately reissues a code for it on the next start without showing a login page — exercise the cancel path from a fresh browser profile.

Chromium treats `http://127.0.0.1` as a trustworthy origin, so the `Secure` and `__Host-` cookies of both services work over the loopback HTTP of this check; production deployments keep the documented HTTPS requirements. Record results with every one-time value (codes, states, handles) and every credential redacted.

A second matrix covers the Testing-only private-network HTTP transport (#214). It needs no loopback exemption: run the same Lexarbor host with `ASPNETCORE_ENVIRONMENT=Testing` and `AdminAuthentication__HttpTestOrigins=http://<host-LAN-IP>:5008` (an RFC1918 or IPv6 ULA literal origin, port included), point the `OidcCode` redirect and post-logout settings at the same origin, and configure SignaCore's matching `security.hosted_login_http_test_origins` (0.1.13+) plus its existing non-HTTPS issuer opt-ins. Verify the same acceptance list as above; additionally the cookies are named `HttpTest-Lexarbor.…` without `Secure`, a leftover `__Host-` cookie authenticates nothing until the administrator signs in again, and configuring the allowlist under a non-Testing environment refuses startup. The automated coverage of this matrix — origin parsing, startup gates, cookie names, the full HTTP login/logout trip and the allowlist boundaries of both callback settings — lives in `HostedLoginHttpTestTransportTests` and `AdminHttpTestHostedLoginTests`.

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

`SystemVersionEndpointTests` covers Cookie/Bearer, custom roles, expired/malformed credentials, exact failure fields, conditional GET, concurrent immutable snapshots, cancellation and the unchanged anonymous health boundary. Missing assembly metadata fallback is covered by `ApplicationVersionTests`.

## Shared word replacement verification

`VocabularyWordEditTests` covers trim normalization with the submitted casing stored as the display value (a casing-only change applies while the normalized key is unchanged), both nullable phonetics, shared enabled/disabled memberships without any meaning changes, unassigned words, historical duplicate normalized spellings (including non-ASCII uppercase, tabs/newlines, and Unicode whitespace), self-exclusion and distinct Unicode spellings, missing/invalid/cancelled requests, and rollback after a SQLite trigger rejects an update. File-WAL tests use independent contexts and a controlled transaction barrier for edit/edit and both edit/import orders; the later successful operation wins and cancellation after admission does not undo the transaction.

`VocabularyWordEditEndpointTests` verifies all required fields and JSON types, unknown fields, preserved values, Cookie/Bearer and custom roles, missing CSRF/401/403, 404/409 without partial writes, and a real external SQLite write lock producing 503 plus `Retry-After: 1`. Run the Release .NET suite and Docker persistence smoke; existing import/public tests must remain green.

Historical Unicode conflict regressions also exercise the administrator word PUT through HTTP: conflicts return 409 and leave spelling and both phonetics unchanged. Domain snapshots verify that timestamps, other words, and meanings remain unchanged.

## Meaning replacement verification

`VocabularyMeaningEditTests` verifies nullable/blank part of speech and example, trimmed definitions, disabled-book editing, multi-meaning/shared-word isolation, 404 resource checks, 409 ownership/equivalence checks, cancellation without writes and rollback of a SQLite constraint failure. File-WAL tests hold a controlled transaction barrier between independent contexts for edit/edit and both edit/import orders, checking the last successful update and completion after cancellation inside the transaction.

`VocabularyMeaningEditEndpointTests` covers three-field presence/types, unknown IDs/fields, Cookie/Bearer and custom roles, 401/403/missing CSRF, all path resource/ownership failures, unchanged data after rejection and an external SQLite write lock returning 503 with `Retry-After: 1`. The full Release suite retains import equivalence/example updates and public detail ordering coverage. Run Docker persistence smoke as well. Combining word/meaning editing with cleanup is covered by `VocabularyEditCleanupCombinationTests` and its endpoint counterpart, described under "Editing combined with cleanup verification".

## Administrator vocabulary query verification

`VocabularyAdminQueryTests` uses real SQLite with enabled A, disabled B, shared polysemy, B-only vocabulary, historical orphans and an empty book. Assertions cover membership completeness, meaning isolation, independent whole-book/matched counts, stable pagination, literal LIKE metacharacters, missing resources and cancellation without writes. A file-WAL test pauses the first read with a command interceptor, lets another context commit a deletion, then verifies that the full response still sees the original snapshot.

The 20,000-word fixture captures actual EF SQL and EXPLAIN output in test output. A 20-item content page executes seven SELECTs and materializes one book, 20 words and 20 meanings; projected memberships are limited to those page IDs. The plan must use the existing word and book/word indexes. This verifies bounded association loading and no per-word query loop, not a latency SLA. HTTP tests cover all three routes with Cookie/Bearer and custom roles, exact DTO fields, 401/403, pagination/404 failures, public disabled-book filtering and the unchanged legacy words endpoint. Run the standard Release .NET suite and container persistence smoke for this database query change.

## Scoped cleanup verification

`VocabularyCleanupTests` exercises all four actions against a synthetic legacy book, shared multi-meaning words, a disabled referencing book, exclusive words and unrelated historical orphans. Preview counts must equal static commit results; clear retains all book properties, delete removes the book, other meanings remain byte-for-byte equivalent, and `PRAGMA foreign_key_check` is empty. Empty scopes, replay rules, changed preview data/name, wrong ownership, stale batch membership, pre-entry cancellation and the legacy DELETE 409 are checked.

Command interceptors inject exceptions immediately after deleting meanings, orphan words, or the book; each must restore all business rows, leave no temporary work set and allow connection reuse. A SQLite trigger verifies constraint rollback. Independent-context file-WAL tests use transaction barriers for import/cleanup and existing single-edit/cleanup in both orders, duplicate cleanups, cancellation between deletion stages, post-clear/post-delete imports and read-only preview during a held writer. A second preview test pauses after the first read, commits a deletion on another context, and checks that later preview counts still use the original snapshot.

The 20,000-word file-WAL test verifies real preview/commit counts, FK integrity, preservation of unrelated rows, five set-operation commands for clear, and only two materialized book entities across preview and commit (no materialized words/meanings). It prints the actual cleanup SQL; no timing is an SLA. HTTP tests cover exact success/failure fields, Cookie/Bearer/custom roles, CSRF/401/403 before parsing, all action shapes and selection limits, fixed-length/chunked 1 MiB boundaries, resource/name conflicts, constraints and an external writer's 503 plus Retry-After. Run the Release .NET suite and Docker build/persistence smoke. Composition of the W/M replacement edits with these cleanup commands is covered by the dedicated combination tests below.

## Editing combined with cleanup verification

`VocabularyEditCleanupCombinationTests` drives the W/M full-replacement edits and the C cleanup commands over one shared file-WAL dataset in both orders, per the parent model's end-to-end scenario 6. A meaning edit followed by any of the four actions deletes the current edited row — the preview and the commit both resolve R against it — while the disabled book's rows stay byte-for-byte, the shared word survives through B, the historical orphan is untouched, and `PRAGMA foreign_key_check` ends empty. Cleanup first makes the later meaning/word edit 404 on the removed ids with no revival and no partial write, and the surviving ownership rules (409 on another book's meaning) stay intact. A word edit followed by removeWords still counts and deletes the current collections; removing a word's last reference makes the later word edit 404, and the still-referenced shared word stays editable. Independent-context tests hold one write inside its transaction while the other operation waits on the shared write lock, checking that either order converges on the same terminal state with no partial write, no dangling reference, and no other book's data touched.

`VocabularyEditCleanupCombinationEndpointTests` repeats the combination on one real pipeline instance with bearer administration auth: a meaning PUT followed by preview and commit removes the edited row and answers 404 on the removed id; cleanup first refuses later meaning/word PUTs without writes; a word PUT followed by clear keeps the disabled book's reference and the edited shared fields while the exclusive word goes; clear first leaves the still-referenced shared word editable. The cleanup browser specification adds the frontend leg: one meaning is edited as a full replacement and then deleted through the same drawer, with the deletion request carrying the same book, word, and meaning ids and the detail and list refreshing after each commit.
