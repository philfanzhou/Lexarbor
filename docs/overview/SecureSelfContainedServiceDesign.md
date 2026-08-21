# Lexarbor secure self-contained service design

## 1. Background

Lexarbor uses .NET 10, ASP.NET Core Minimal APIs, EF Core 10, SQLite, Vue 3, TypeScript, Element Plus, and Vite. The backend hosts the frontend's static files, and the Docker image produces both at build time.

The following problems existed before this design was implemented, and each is addressed in a section below:

- `/admin/*` had no authentication or role check.
- The frontend had no login state, so an anonymous visitor could see and call the administration features directly.
- A meaning's `BookId` was nullable and had no book foreign key.
- Updating a non-existent word, meaning, or book could turn into an insert.
- Word normalization and duplicate meaning imports had no complete constraint.
- A disabled book could still appear in the public list, and the deletion strategy did not protect a book in use.
- Chinese-to-English distractors were drawn from the whole vocabulary, so a question could be polluted across books or contain duplicate options.
- Several endpoints returned `Exception.Message` to the client directly.
- Book search, filtering, and paging loaded the whole table first.
- The repository-level `PROJECT.md` recorded an old port that disagreed with the 5008 actually in use.
- Vocabulary lacked a complete formal documentation entry point and a `docs/frontend/README.md`.

This design keeps the paths and business behaviour of the four existing `/api/*` endpoints and completes authentication, data boundaries, error handling, query performance, frontend login, and the verification system. ADR-003 separately approved the later change from a single phonetic field to a British and American pair.

## 2. Design goals

1. Vocabulary continues to deploy self-contained as one service, one image, one port.
2. The administration page proxies the Identity administrator login through the Vocabulary backend.
3. Only a user whose Identity JWT contains `role=admin` can reach the administration endpoints.
4. The frontend never touches the Identity address, the AppSecret, an access token, or a refresh token.
5. The existing `/api/*` paths and business semantics are preserved; the phonetic fields change to a British and American pair per ADR-003.
6. Words, meanings, and books get reliable SQLite constraints and a first-run database creation flow.
7. Every HTTP result uses the shared `VocabularyHttpResponse` envelope.
8. Filtering, counting, paging, and random candidate selection happen in SQLite wherever possible.

## 3. Non-goals

- Do not change Identity's administrator bootstrap account or its `role=admin` injection.
- Do not move the Vocabulary administration page into the Admin Portal.
- Do not reuse the administrator cookie as service-to-service authentication for the public `/api/*`.
- Do not introduce a frontend state management library or a new UI framework.
- Do not implement automatic refresh token renewal; an administrator logs in again once the JWT expires.
- Do not provide a PostgreSQL to SQLite data migration; the service has not shipped and there is no existing database.

## 4. Overall architecture

Vocabulary continues to listen on container port 5008 and serves the following from one ASP.NET Core host:

| Capability | Path | Access boundary |
|------|------|----------|
| Vue static files and the SPA | `/`, static assets, frontend hash routes | Anonymous |
| Health check | `GET /health` | Anonymous |
| Administration login | `POST /admin/auth/login` | Anonymous |
| Session state | `GET /admin/auth/session` | `role=admin` |
| Administration logout | `POST /admin/auth/logout` | Anonymous, repeatable |
| Public business API | The existing `/api/*` | Anonymous, kept compatible |
| Administration API | The existing `/admin/*` other than the authentication entry points | `role=admin` |

An unknown `/api/*` or `/admin/*` path answers 404 in the shared envelope and does not fall through to the SPA. The SPA fallback stays anonymously accessible, which avoids the deadlock of being unable to load the login page while logged out.

## 5. Identity administrator authentication

### 5.1 Configuration

The Vocabulary backend uses the following configuration:

```json
{
  "IdentityService": {
    "Authority": "http://localhost:8080",
    "Issuer": "http://localhost:8080",
    "Audience": "lexarbor"
  },
  "AdminAuthentication": {
    "CookieName": "lexarborAdmin",
    "CookieSecure": false,
    "Provider": "Oidc",
    "RequiredRole": "admin",
    "Oidc": {
      "TokenEndpoint": "",
      "ClientId": "",
      "ClientSecret": "",
      "Scope": "openid profile"
    },
    "Gateway": {
      "TokenPath": "/api/auth/token"
    }
  }
}
```

Administrator login goes through the `IAdminCredentialAuthenticator` abstraction, whose implementation is selected by `AdminAuthentication:Provider`; see [ADR-001](../adr/ADR-001-pluggable-admin-authentication.md):

- `Oidc` (default): the standard OAuth2 password grant, configured in `AdminAuthentication:Oidc:{TokenEndpoint,ClientId,ClientSecret,Scope}`; when `TokenEndpoint` is left empty it is resolved from the discovery document.
- `Gateway` (optional): the compatible JSON password-token contract, with credentials from `AdminAuthentication:Gateway:{AppId,AppSecret}`.

Configuration conventions:

- `IdentityService:*` describes which issuer is trusted and comes from appsettings, standard .NET environment variables, or any other standard configuration provider; a container deployment maps Authority through `LEXARBOR_IDENTITY_AUTHORITY`.
- Provider credentials are injected from environment variables only and are never written into the frontend or the repository's default configuration.
- `AdminAuthentication:Gateway:Authority` is optional; when empty it falls back to `IdentityService:Authority`, which serves deployments whose login endpoint and JWKS endpoint are not same-origin.
- The service still starts when the selected provider's credentials are missing; a login request answers 503 in the shared envelope and the configuration error is logged without any secret.
- `Issuer`, `Audience`, the signature, public key rotation, and expiry are validated by JWT Bearer.
- The JWT handler has inbound claim mapping turned off. The role claim is accepted both as the short name `role` and as the full `ClaimTypes.Role` URI, which covers how common OIDC and .NET issuers serialize claims. The administrator policy requires `AdminAuthentication:RequiredRole` (default `admin`), which `AdminRoleHandler` reads at evaluation time.
- A local HTTP development environment may use `CookieSecure=false`; a TLS deployment must configure `true`.

### 5.2 Login flow

1. The browser submits `{ username, password }` to `POST /admin/auth/login`.
2. Vocabulary validates the required fields and logs neither the request body, the password, nor the username and password combination.
3. The Lexarbor backend calls the token endpoint obtained from OIDC discovery, or the explicitly configured endpoint.
4. The OIDC provider uses a form-urlencoded password grant; the optional Gateway provider uses the following JSON:

   ```json
   {
     "grantType": "password",
     "username": "<submitted username>",
     "password": "<submitted password>"
   }
   ```

5. Only the Gateway provider adds the server-configured `X-Admin-AppId` and `X-Admin-AppSecret` request headers.
6. When the provider rejects the credentials, Lexarbor answers 401 with a generic credential error and does not pass the upstream's internal message through.
7. Once Identity returns success, Vocabulary validates the access token with the same issuer, audience, signing key, and lifetime parameters used for request authentication; an invalid token or one that does not match the configuration answers 502 and sets no cookie.
8. Vocabulary decides administrator identity from the `role` claim of the validated JWT; an ordinary user gets 403 and no cookie.
9. The administrator JWT is written into the `lexarborAdmin` cookie, which uses:
   - `HttpOnly=true`
   - `SameSite=Strict`
   - `Path=/`
   - `Secure`, controlled by `AdminAuthentication:CookieSecure`
   - `Max-Age`, taken from Identity's `expiresIn`; when the response carries no usable value it falls back to one hour, and the JWT's own expiry is still validated independently
10. The login response returns only the success status and the non-sensitive display information the validated JWT requires, never an access token or a refresh token.
11. A refresh token returned by Identity is discarded immediately and is persisted by neither the frontend nor Vocabulary.

An Identity timeout, network error, or missing usable response answers 502; missing configuration answers 503. No log may contain a password, JWT, cookie, AppSecret, or a full Identity response.

### 5.3 Request authentication

JWT Bearer extracts the token in this order:

1. `Authorization: Bearer <token>`
2. the `lexarborAdmin` HttpOnly cookie

The bearer channel serves automated tests and controlled API calls; the administration frontend uses the cookie only. Every administration endpoint uses the authorization policy named `VocabularyAdmin`, which requires an authenticated principal carrying `role=admin`.

An authentication challenge and a forbidden response return, respectively:

```json
{ "success": false, "message": "Authentication is required." }
```

```json
{ "success": false, "message": "Administrator access is required." }
```

with status codes 401 and 403.

### 5.4 Logout and sessions

- `GET /admin/auth/session` is protected by the `VocabularyAdmin` policy and lets the frontend initialize its login state.
- `POST /admin/auth/logout` is callable anonymously and always deletes the cookie of that name, so an expired or corrupted cookie can be cleared too.
- After logout the browser no longer carries the JWT, and a further administration request answers 401.
- A JWT is a stateless token, so deleting the cookie is not a server-side global revocation; because the refresh token is not stored, the browser cannot restore the session by itself.

### 5.5 CSRF protection for cookie write requests

A `POST`, `PUT`, `PATCH`, or `DELETE` administration request authenticated by cookie rather than by an Authorization header must include:

```text
X-Requested-With: XMLHttpRequest
```

The frontend Axios instance adds that header for all of them. The service opens no CORS policy that lets an arbitrary origin send credentials. Together with `SameSite=Strict`, this check reduces the risk of a cross-site form submission.

The login and logout entry points do not depend on that header; login does not use an existing authentication cookie, and logout only performs an idempotent cookie deletion.

## 6. Route permission matrix

### 6.1 Public business endpoints

The following existing endpoints remain anonymously accessible:

```text
GET  /api/vocabulary/{wordId}
GET  /api/vocabulary
POST /api/vocabulary/question
GET  /api/vocabulary-books/all
```

No administrator cookie or new service-to-service authentication is introduced for them here. Should service-to-service authentication be needed later, the real callers should be confirmed first and a separate credential designed.

### 6.2 Administration endpoints

All of the following require `role=admin`, read endpoints included:

```text
POST   /admin/vocabulary
POST   /admin/vocabulary-books
PUT    /admin/vocabulary-books
GET    /admin/vocabulary-books/{id}
GET    /admin/vocabulary-books
GET    /admin/vocabulary-books/by-category
GET    /admin/vocabulary-books/categories
GET    /admin/vocabulary-books/education-levels
GET    /admin/vocabulary-books/grades
GET    /admin/vocabulary-books/grades-by-level
GET    /admin/vocabulary-books/{id}/words
DELETE /admin/vocabulary-books/{id}
```

## 7. Data relationships and the initial SQLite migration

### 7.1 The formal relationships

```text
VocabularyBook 1 ─── * VocabularyMeaning * ─── 1 Vocabulary
```

- `VocabularyMeaning.VocabularyId`: required, a foreign key to `Vocabulary`, cascading so that deleting a word deletes its meanings.
- `VocabularyMeaning.BookId`: required, a foreign key to `VocabularyBook`, restricting the deletion of a book.
- Before adding a meaning, the book must be confirmed to exist and be enabled.
- The public detail and question queries must confirm the book exists and is enabled.

### 7.2 A single initial migration

The service has not shipped and there is no existing PostgreSQL data, so the migration history is rebuilt starting from one SQLite `InitialCreate`. A new database gets the non-nullable `book_id`, both foreign keys, the delete behaviours, the query indexes, the British and American phonetic columns, and the equivalent-meaning unique index directly. The EF Core model, the migration, and the model snapshot must stay in agreement.

At startup the database file's existence is checked before migrating. When the file is absent, the 300-word starter book is written from the embedded TSV in one transaction after migrating; when the file exists it is migrated only and the seed is not written again.

## 8. Word and meaning writes

### 8.1 Word normalization

Every write and every lookup by name uses the same normalization rule:

```text
normalizedWord = word.Trim().ToLowerInvariant()
```

- A value that normalizes to whitespace answers 400.
- A new word is stored in its normalized form.
- Lookups use the equivalent database expression, so `Apple`, ` apple `, and `APPLE` cannot be imported as duplicates.
- A unique index protects newly written normalized words.
- The lookup by normalized word compares against the generated `normalized_word` column, which carries `lower(trim(word))` and an index. Writing the expression into the predicate instead puts a function around the column, which SQLite answers by reading every row -- once per imported word, while the process-wide write lock is held.
- The word DTO uses the two nullable strings `phoneticUk` and `phoneticUs`; the old `phonetic` field is neither accepted nor returned.

### 8.2 Create and update semantics

- When the request carries a word ID and the word does not exist, the answer is 404 and no word is created.
- When the request carries no word ID, the word is looked up by normalized name and created only if absent.
- When the request carries a meaning ID and the meaning does not exist, the answer is 404.
- Before updating a meaning, its `VocabularyId` and `BookId` must be confirmed to match the current word and book.
- A meaning ID must not be used to rewrite another word's or another book's meaning into the current data.
- When adding a meaning, a missing book answers 404 and a disabled book answers 422.
- The same `VocabularyId`, `BookId`, normalized part of speech, and trimmed definition are treated as the same meaning.
- A repeated import succeeds and reuses the existing meaning; when a new British phonetic, American phonetic, or example is supplied it is applied under the existing update semantics rather than inserting a duplicate row.
- A SQLite deployment is limited to a single instance. A process-level write lock serializes every write, taken by both the transaction helper and the plain save so that no write path can bypass it, and the logical key's unique index over the stored generated columns is the database backstop. The lock is held per async flow, so a save nested inside a transaction joins the lock its caller holds rather than waiting on it.
- The database runs in WAL mode, so a read never blocks a write. A write that still finds the database held by another connection answers 503 with `Retry-After`, not 409 and not 500: nothing is wrong with the request and retrying it works.
- A unique constraint violation, a concurrent duplicate, or any other database consistency conflict answers 409.

`Category` keeps the existing string field as its formal representation. The integer `BookCategories` constant type, which disagreed with it and was unused, is deleted so that no dual representation is maintained.

## 9. Book state and deletion

- Creating a book must use an empty ID; the service generates a new one.
- Updating a book must carry an ID; a missing target answers 404.
- Updating a book replaces every field rather than merging, so `bookName`, `displayOrder`, and `status` must all be sent. Omitting one answers 400 instead of writing the field's default over the stored value, because those defaults blank the name, reorder the book, and disable it.
- `Status=true` means enabled and `Status=false` means disabled.
- The administration list contains both enabled and disabled books.
- `GET /api/vocabulary-books/all` returns enabled books only.
- Once a book is disabled, the public vocabulary detail and question endpoints no longer return its meanings.
- An administrator can still view a disabled book and its words, so it can be restored or maintained.
- Deleting a non-existent book answers 404.
- A book with no related meanings may be hard deleted.
- A book with any related meaning refuses the hard delete with 409, and the administrator should disable it instead.
- The book foreign key uses `ON DELETE RESTRICT`, so the database is the last line of defence against orphaned meanings.

## 10. Question generation

`POST /api/vocabulary/question` keeps its request and response DTOs and generates a four-option question by these rules:

1. The target book must exist and be enabled.
2. The target word must exist and must have at least one meaning in the target book.
3. Chinese to English:
   - the stem is the current meaning;
   - the correct answer is the current word;
   - the three distractor words are selected from the same book only, and a word that carries an equivalent definition in that book is excluded, because such a word answers the stem correctly and cannot be a wrong option.
4. English to Chinese:
   - the stem is the current word;
   - the correct answer is the current meaning;
   - the three distractor definitions are selected from the same book only.
5. Candidates are deduplicated by `VocabularyId`, so each distractor word contributes at most one option.
6. The repository query first excludes the current word and everything equivalent to the correct answer -- the answer text itself, and in the Chinese-to-English direction any other word sharing the stem's definition -- then deduplicates by normalized option text, and only then randomly limits the result to three distractors. Equivalence is `lower(trim(...))` on both sides in both directions.
7. A question is generated only when three distinct valid distractors are obtained; limiting before deduplicating, which would falsely report too few candidates, is not allowed.
8. Too few candidates answers 422 with a short business error.
9. The four final options are shuffled.

The repository samples candidates rather than sorting the book. Every vocabulary id is a v4 GUID, so id order is already an arbitrary, uniform shuffle: the query starts at a random point in that order and reads a bounded window forward, wrapping to the start if the probe landed near the end. An index answers both, so the cost of a question does not depend on the size of the book it was asked about.

The window can come back short -- the book may genuinely be near the end of its candidates, or the drawn words may all have been ineligible. Because a short result is what produces the 422 in rule 8, the repository then runs the exhaustive `random()`-and-window-function query it used to run on its own. That keeps 422 meaning "this book cannot produce a question" rather than "the sample was unlucky", at the cost of one slow query in exactly the case where the fast one proved nothing.

## 11. Queries and paging

- A missing or zero `page` is treated as 1 for compatibility.
- A missing or zero `size` is treated as 20 for compatibility.
- `page<0`, `size<0`, `size>100`, or a value that would overflow the paging arithmetic answers 400.
- Word search performs its filtering, ordering, counting, and paging on the database side.
- Book search performs its name and description filtering, ordering, counting, and paging on the database side.
- Keyword matching is case-insensitive. It uses SQLite's `LIKE`, whose default case folding covers ASCII, which is the same folding `lower()` gives the normalization and question queries. `%`, `_`, and the escape character are escaped in the keyword and matched literally, so a keyword can never widen its own search. Case outside ASCII is not folded: `CAFÉ` does not find `café`. Folding the rest of Unicode would need FTS5 or an ICU build and is not done.
- Categories, education levels, grades, and grades by education level perform their filtering and deduplication on the database side.
- The words in a book are selected as distinct words through the meaning relationship on the database side, rather than loading every meaning and then looking words up.
- An administration query may reach disabled books; a public query can only read through an enabled book's relationships.

## 12. Shared error handling

Every HTTP result uses:

```json
{ "success": true, "data": {} }
```

```json
{ "success": false, "message": "A concise public message." }
```

Status code conventions:

| Status code | Situation |
|--------|------|
| 400 | Invalid parameters, JSON, or paging bounds |
| 401 | Wrong credentials, or a missing or invalid JWT |
| 403 | Authenticated but not an administrator |
| 404 | The word, meaning, or book does not exist |
| 409 | A unique constraint, a related deletion, or another data conflict |
| 422 | A business precondition such as a disabled book or too few question candidates |
| 500 | An unexpected exception |
| 502 | Identity is unreachable or returned an invalid response |
| 503 | Production administrator login configuration is missing, or the database is held by another writer |

Endpoints no longer catch a general exception and return `ex.Message`. The shared exception middleware is responsible for:

- mapping known domain exceptions to 400, 404, 409, or 422;
- mapping database unique and foreign key constraint violations to 409;
- mapping JSON parsing errors and the Minimal API `BadHttpRequestException` to 400;
- logging an unexpected exception at Error level with the exception object and returning the generic 500 message;
- using Warning for an expected NotFound;
- producing the same envelope for authentication challenges, forbidden responses, and unknown API paths.

No client response may contain SQL, a connection string, a stack trace, a database exception, or any internal implementation detail.

## 13. Frontend design

The frontend continues to use Vue 3, TypeScript, Element Plus, Axios, Vue Router, and Vite, and adds neither Pinia nor another dependency.

### 13.1 Routes and state

- `/login`: the username and password login page.
- `/forbidden`: the non-administrator notice page.
- `/books`: book management.
- `/import`: word import.
- The application calls `GET /admin/auth/session` at startup to restore the login state.
- The route guard redirects to the login page while unauthenticated.
- The administration navigation and pages render only in the administrator state.
- The login page contains no default username or password.
- The header carries a logout button.

Authentication state is maintained by a small TypeScript module or composable; no new global state library is introduced.

### 13.2 Axios behaviour

- Use same-origin relative paths and configure no Identity address.
- Authenticate by cookie and never read or write an access token.
- Add `X-Requested-With: XMLHttpRequest` to administration write requests.
- A shared typed `ApiError` carries the HTTP status and the public message.
- 401: clear the local authentication state and redirect to the login page.
- 403: redirect to the forbidden page.
- 400, 404, 409, 422, 500, 502, 503: the page shows an error message suited to the user.
- A component's catch parameter is `unknown`, never `any`.

The existing features, fields, and Vite build of book management and word import all remain usable.

## 14. Deployment

- The Vocabulary container's HTTP port is fixed at 5008.
- The Vue build output continues to be copied into the backend's `wwwroot` by the Vocabulary Dockerfile.
- `scripts/start.sh` maps only explicitly supplied deployment variables into .NET configuration:

  ```text
  LEXARBOR_ADMIN_AUTH_PROVIDER → AdminAuthentication__Provider
  LEXARBOR_OIDC_CLIENT_ID → AdminAuthentication__Oidc__ClientId
  LEXARBOR_OIDC_CLIENT_SECRET → AdminAuthentication__Oidc__ClientSecret
  LEXARBOR_IDENTITY_AUTHORITY → IdentityService__Authority
  LEXARBOR_COOKIE_SECURE → AdminAuthentication__CookieSecure
  LEXARBOR_DATA_DIR (defaults to the service's data/) → the /app/data persistent volume
  ```

- `IdentityService:Authority` comes from the persistent configuration and can also be overridden by an explicit `LEXARBOR_IDENTITY_AUTHORITY`.
- The container mounts `LEXARBOR_DATA_DIR` at `/app/data`. On first startup the image's built-in configuration is copied to `/app/data/appsettings.json`, and an existing file is left unchanged; the same directory holds the default `vocabulary.db`, so no second mount is needed.
- Configuration precedence is the image defaults, the persistent `appsettings.json`, explicit environment variables, then command-line arguments.
- The provider's client credentials are passed to Lexarbor by the deployment environment and are never printed to the console.
- A TLS deployment sets `AdminAuthentication__CookieSecure=true`.
- The Identity provider must have the Lexarbor client registered in advance, must permit the current password grant, and must supply the administrator role in the JWT.

## 15. Testing and verification

### 15.1 Authentication and authorization

- Reaching each class of administration endpoint while logged out answers a 401 envelope.
- Reaching an administration endpoint with an ordinary user's JWT answers a 403 envelope.
- When fake Identity returns the bootstrap admin role, login succeeds and sets the HttpOnly cookie.
- A wrong username or password answers 401 and sets no cookie.
- An ordinary user's login answers 403 and sets no cookie.
- The administrator cookie can reach the book and word administration endpoints.
- Logout deletes the cookie; afterwards an administration endpoint answers 401.
- An Authorization bearer administrator token can also reach the administration endpoints.
- The existing anonymous behaviour of `/api/*` stays compatible.
- A cookie write request without the CSRF protection header is refused.

### 15.2 Domain and data

- A meaning cannot be added to a missing or disabled book.
- Updating a non-existent word, meaning, or book answers NotFound.
- A meaning that does not belong to the current word or book cannot be updated.
- A normalized word is created only once.
- A repeated meaning import does not insert a second row.
- Deleting a book in use answers Conflict.
- An empty book can be deleted.
- A disabled book does not appear in the public list, and the public detail and question endpoints cannot read its meanings.
- The EF model contains both foreign keys from meaning to word and book, with the correct delete behaviours.
- Real SQLite verifies first-run creation of a missing database, the 300-word seed, migrate-only for an existing database, and idempotent concurrent writes.

### 15.3 Question generation

- Chinese-to-English distractor words come only from the current book.
- English-to-Chinese distractor definitions come only from the current book.
- Distractors never contain the correct answer.
- A word with several meanings does not produce duplicate distractor options.
- Data from different books never pollutes another book.
- Fewer than three valid distractor words answers 422.

### 15.4 HTTP and error handling

- Verify the status and the shared envelope for 400, 401, 403, 404, 409, 422, 500, 502, and 503.
- The fake Identity HTTP handler verifies the request fields and the server-side AppId and AppSecret headers.
- Simulate a repository exception and confirm the 500 response contains no internal exception message.
- An unknown `/api/*` answers a 404 envelope; an unknown `/admin/*` answers 401 for an anonymous request and a 404 envelope for an administrator.
- The SPA, static files, the login page, and the health check stay anonymously accessible.

### 15.5 Delivery verification commands

```bash
dotnet build Lexarbor.sln --configuration Release
dotnet test Lexarbor.sln --configuration Release --no-build
```

```bash
cd frontend
npm run test:types
npm run build
```

When Identity and Vocabulary can both be run, add HTTP smoke tests for login, the cookie, logout, the public API, the persistent volume, and the migration state. When real deployment credentials are unavailable, the automated integration tests use fake Identity and no real integration result is invented.
