# Hosted login release notes

## Breaking changes

The password proxy login has been removed (#175). The SignaCore hosted login
page is the only administrator sign-in, and Lexarbor no longer receives or
forwards administrator passwords anywhere.

- `POST /admin/auth/login` no longer exists. It is answered like any unknown
  `/admin/*` route: 401 for anonymous callers, 404 for authenticated
  administrators.
- `AdminAuthentication:Oidc`, `AdminAuthentication:Gateway`,
  `AdminAuthentication:CookieName` and `AdminAuthentication:CookieSecure` are
  gone, together with the `LEXARBOR_OIDC_{TOKEN_ENDPOINT,CLIENT_ID,CLIENT_SECRET,SCOPE}`,
  `LEXARBOR_GATEWAY_*`, `LEXARBOR_ADMIN_AUTH_PROVIDER` and
  `LEXARBOR_COOKIE_SECURE` container variables.
- `AdminAuthentication:Provider` no longer selects anything. It must be unset
  or exactly `OidcCode`; any other value stops startup with an English
  diagnostic pointing at the migration steps in
  [Deployment](Deployment.md#administrator-sign-in-hosted-login-only).
- The old `lexarborAdmin` JWT cookie is no longer authenticated. Its lifetime
  was at most one hour, so every issued cookie has long expired naturally;
  logout and hosted sign-in still delete it from the browser.
- `GET /admin/auth/method` no longer exists (#176). The administration UI
  offers the SignaCore navigation directly, and the deleted route is answered
  like any unknown `/admin/*` route: 401 for anonymous callers, 404 for
  authenticated administrators.
- `GET /admin/auth/start` keeps its per-IP `admin-login` rate limit and its
  positive-integer `Retry-After` header.
- Every routed `/admin/*` response (#177) — authentication routes, the business
  administration API, the system version endpoint and the unknown-route
  catch-all — now carries the ServiceMantle mandatory security-header baseline
  (`Cache-Control: no-store`, `Pragma: no-cache`, `X-Content-Type-Options:
  nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer` and a
  lock-down `Content-Security-Policy`) on every answer, replacing the
  route-by-route handwritten `no-store`/`no-referrer` headers. `/api/*`,
  `/health*` and the SPA are unchanged; see
  [Deployment](Deployment.md#administration-response-headers).

Upgrade steps: register a Confidential application with the identity provider
(per-application audience, exact HTTPS callback), configure the
`LEXARBOR_OIDC_CODE_*` variables, remove the retired variables listed above,
and restart. A persisted `appsettings.json` that still carries
`"Provider": "Oidc"` or `"Gateway"` must be edited by hand.

Selecting the `OidcCode` provider originally disabled `POST /admin/auth/login`
before password JSON binding, returning 400 with `Password login is disabled for
hosted authentication.` Successful Code login creates an encrypted server-side
session and sends only
an opaque Secure, HttpOnly cookie, bounded by the verified access token expiry.
It revokes the presented old opaque session atomically and deletes the legacy JWT
cookie only after confirmed commit. Failures before commit preserve existing
sessions; lost commit/response results remain unknown and are never retried.

Public `start` and `callback` routes support backend hosted login (the
`method` probe route has since been removed; see the breaking changes above).
The administration UI rollout (#155) switched the frontend to them; see
[Administration UI rollout](#administration-ui-rollout).
Deployment registration, independent Code configuration, proxy query log
suppression and rollback are documented in
[Deployment](Deployment.md#hosted-authorization-code-login).

Exception-generated failure responses changed shape (#144). An exception that
escapes an endpoint before the response has started is no longer answered with
the `{ "success": false, "message": "..." }` envelope; it is answered with a
ServiceMantle `application/problem+json` document carrying `type`, `title`,
`status`, `errorCode` and `correlationId`, and every response also carries the
correlation id as an `x-correlation-id` header. Status codes are unchanged, the
busy-database `503` keeps `Retry-After: 1`, and endpoint-explicit failures —
including the batch import's per-entry `errors` — keep the legacy envelope, so
clients that parsed only `message` need to read `title` for these responses.
The two failure shapes and the full exception mapping table are documented in
[Error handling](ErrorHandling.md#two-failure-shapes).
The health endpoints changed shape and grew (#180). `GET /health` no longer
answers the old `{"success":true,"data":{"status":"healthy"}}` envelope; it and
the new `GET /health/ready` answer the ServiceMantle readiness JSON (`status`,
`phase`, `migrationStatus`, `databaseStatus`, `errorCode`), and the new
`GET /health/live` answers liveness alone. All three are anonymous and
unmetered. Readiness reflects the startup migration result plus one bounded
read-only database probe, so an unreachable or not-yet-migrated database
answers 503 instead of a blind healthy 200. The container `HEALTHCHECK` now
probes `/health/ready`, which makes a container whose database is unreachable
turn unhealthy. External monitors that parsed the old envelope must read the
new fields or use `/health/live`; see
[Deployment](Deployment.md#health-and-smoke-checks).

Database startup preparation became strict (#181). The hand-written
initializer was replaced by the ServiceMantle SQLite startup gate and migration
orchestration; migration content, connection strings and the
`Database:InitializeOnStartup` switch are unchanged. The behavioral changes an
operator can observe:

- A database path that resolves through a symbolic link, a hard-linked
  database file, a `file:` URI or `:memory:` data source, or a read-only
  connection mode now stops startup with
  `database_target_preparation.invalid_target` before anything is opened.
- A database whose `__EFMigrationsHistory` records a migration the running
  build does not know now stops startup with `migration.version_too_new`
  instead of attempting to run.
- Leftover `vocabulary.db-wal`/`vocabulary.db-shm` files after a crash are
  replayed and checkpointed once on the next start, which keeps every
  committed transaction; sidecars without the main file stop startup with
  `database_target_preparation.target_conflict` instead of being adopted.
  (The later rollback-journal switch moved this conversion to the startup
  normalization that runs before the gate observes the target; the
  committed-transaction guarantee is unchanged.)
- A pre-mounted file that is not a SQLite database stops startup with
  `database_target_preparation.connection_failed` and is left byte-for-byte
  unchanged.
- Startup failure log lines carry these ServiceMantle error codes only — no
  path, connection string, or SQL.

Upgrade steps: keep the data directory free of symbolic links, and compare the
newest `MigrationId` in `__EFMigrationsHistory` with the target image before
rolling one back; see
[Database](Deployment.md#database) and
[the database contract](../database/README.md#first-run-creation).

## Prepared upstream logout

In `OidcCode` mode, `POST /admin/auth/logout` now also prepares a SignaCore logout
after the local session has been revoked. When the preparation succeeds, the
unchanged 200 envelope gains an optional `data.logoutUrl` holding a verified,
one-time upstream logout URI; old callers can ignore the field, and every other
mode keeps the exact previous response. A missing `logoutUrl` in hosted mode means
local-only logout: the identity provider may still hold a session. Code-mode
logout responses now carry `Cache-Control: no-store`.

A new anonymous route `GET /admin/auth/logout/return` completes the browser's
return from SignaCore with fixed redirects `/#/login?reason=logged_out` or
`/#/login?reason=logout_failed`, `no-store` and `no-referrer`. The optional
`AdminAuthentication:OidcCode:PostLogoutRedirectUri` setting
(`LEXARBOR_OIDC_CODE_POST_LOGOUT_REDIRECT_URI`) must be registered byte-for-byte
as the application's post-logout URI. Logout never revokes already issued access
tokens; they remain valid until their exact `exp`. Full semantics, guarantees and
proxy masking live in
[Deployment](Deployment.md#prepared-upstream-logout).

## Administration UI rollout

Issue #155 switched the administration frontend to the hosted flow; the change is
deliberate and mode-scoped, and no backend contract above changed.

- The login page renders the SignaCore navigation directly, with no password
  field and no `method` probe anymore (#176): hosted sign-in is the only mode,
  so there is nothing left to detect or fall back from.
- The SignaCore button performs a top-level browser navigation to
  `GET /admin/auth/start`, forwarding only a `returnUrl` from the documented route
  allowlist. One activation starts exactly one navigation. The callback round trip
  is backend-driven; the browser stores no code, state, access token, ID token or
  client secret, and the version request and global 401/403 handling are unchanged.
- The login page consumes the six fixed `reason` values — `canceled`, `denied`,
  `sign_in_failed`, `provider_unavailable`, `logged_out`, `logout_failed` — each
  with one message; an unknown value stays harmless.
- Logout consumes the envelope's optional `data.logoutUrl`: when present it hands
  the browser to that one-time URI as a top-level navigation and no local
  navigation overrides it. When absent, the local session has still ended and
  the UI says the provider may still hold a session and never claims SignaCore
  signed out. Legacy-cookie expiry and rollback are documented in
  [Deployment](Deployment.md#hosted-authorization-code-login).

## Administrator authentication audit

Administrator sign-in, failed sign-in and logout are now persisted as management
audit events (#183) in the new `service_audit_logs` table (additive
`AddServiceAuditLogs` migration; existing databases upgrade through the startup
pending-migration path). This is a new capability, not a contract change: every
API route, response shape, cookie and rate limit stays exactly as it was.

- `admin_login.succeeded` — a completed hosted sign-in; the row commits in the
  same database transaction as the session it created.
- `admin_login.failed` — exactly one row per failed login callback, with the same
  fixed `reason` value the browser redirect carries; the operator is anonymous.
- `admin_login.logout` — a logout that revoked a live session; the row commits
  with the revocation. Idempotent logout without a live session still writes
  nothing and keeps its unchanged 200.

Audit saves never fail silently: a login or logout whose audit row cannot be
written answers the existing fixed session-storage 500/503 instead of succeeding
unaudited, and a failed login whose audit row cannot be saved answers the same
way instead of the failure redirect. Rows never contain codes, state values,
tokens, the client secret, session handles or provider error descriptions; the
trusted client IP and the request correlation id are recorded when available.
The table has no built-in retention or query API — backup and pruning are the
operator's responsibility. Events, fields and the retention contract are
documented in [Deployment](Deployment.md#management-audit-log) and
[the database contract](../database/README.md#management-audit-log).

## Private-network HTTP testing transport

Issue #214 added an explicit, Testing-only plain-HTTP transport for the hosted
administrator login, for isolated test networks that reach the administration UI
directly over HTTP on private IP addresses — no HTTPS proxy, certificate or
localhost tunnel. It is opt-in through one new setting and otherwise changes
nothing:

- `AdminAuthentication:HttpTestOrigins`
  (`LEXARBOR_HOSTED_LOGIN_HTTP_TEST_ORIGINS` through the container script) holds a
  semicolon-separated allowlist of exact private-IP HTTP origins
  (`http://<literal-IP>:<port>`, explicit port 1–65535, RFC1918 IPv4 or IPv6
  unique-local only), entry-for-entry compatible with SignaCore 0.1.13's
  `security.hosted_login_http_test_origins`. The full syntax, boundaries and the
  two-service relationship are documented in
  [Deployment](Deployment.md#plain-http-deployments).
- Enabled only when the actual host environment is exactly `Testing` and the parsed
  list is non-empty; a non-empty allowlist outside Testing stops startup with a
  diagnostic naming the setting. Missing or empty keeps the default HTTPS contract
  byte-for-byte.
- With it enabled, `AdminAuthentication:OidcCode:RedirectUri` and
  `PostLogoutRedirectUri` may use an allowed HTTP origin, and the session and both
  transaction cookies are issued as `HttpTest-Lexarbor.…` (HttpOnly, SameSite=Lax,
  Path=/, no Domain, no Secure) instead of `__Host-Lexarbor.…`. Opaque handles,
  browser binding, one-time consumption, PKCE, audits and token validation are
  unchanged; a leftover cookie from the other mode is not read, so switching modes
  requires signing in again. The database, key ring, container volumes and every
  public API shape are untouched.
- Plain HTTP has no confidentiality or integrity — the authorization code and
  cookie handles travel unencrypted. The deployment must stay on an isolated test
  network with access control; the startup log states this at warning level.

## Shared rate-limit migration

ServiceMantle 0.3.0 now supplies `admin-login` and `public-api` with six-segment
sliding windows. Default quotas and the protected routes stay the same, but
boundary bursts differ from the former fixed window. 429 is now
`application/problem+json` with `title: Too many requests.`,
`errorCode: rate_limit.exceeded`, status and correlation id; no address or bucket
key is exposed. The hosted start is a top-level browser navigation, so a rejected
navigation displays this safe document directly. No frontend secret is needed.
The frontend reads `title` and treats 429 separately from 401/403 redirects.

Enabled policies accept `PermitLimit=1..10000` and `WindowSeconds=10..600`;
prepare any out-of-range deployment configuration before upgrading. Disabled
policies ignore invalid numeric values and retain their startup warning. Settings
are fixed at startup. `Retry-After` keeps any shared value, otherwise recommends
one configured whole window; concurrent traffic can still use the next quota.
The limiter is process-local with no queue, and is not distributed protection or
a WAF. Rolling back application code restores the old fixed window, envelope and
numeric range, without migrating data or changing persistent directories.

## Migration to the official SignaCore client package (#209)

The in-house hosted-login implementation (OIDC authorization code + PKCE,
pending stores, prepared logout, the cookie double-submit CSRF middleware) has
been replaced by the official
[`SignaCore.Client.AspNetCore` 0.1.14](https://www.nuget.org/packages/SignaCore.Client.AspNetCore)
package. Lexarbor keeps its own business rules — the administrator role gate
(now the package's pre-sign-in authorization extension point), the ServiceMantle
management audit rows, the management identity mapping, and the SQLite session
rows (as the package's server-side ticket store).

Session continuity: the `__Host-Lexarbor.AdminSession` cookie name, the handle
format and the protected payload format are unchanged, so sessions signed in
before the upgrade keep authenticating — and a rollback of this migration keeps
the sessions signed in after it.

Declared changes of the migration:

1. **CSRF moves to the official antiforgery token model (B6).** The frontend
   fetches `GET /admin/auth/csrf` (a new public route answering
   `{"token": ...}` with the browser-bound antiforgery cookie) and echoes the
   token in the `X-SignaCore-CSRF` header on every unsafe method, the hosted
   logout included. The `X-Requested-With` double-submit header and the
   `CookieCsrfMiddleware` are gone.
2. **CSRF failure presentation.** A session write with a missing or wrong token
   fails the session authentication itself and is answered with the management
   API's fixed `401 {"success":false,"message":"Authentication is required."}`
   (previously `403` with a CSRF-specific message). The logout endpoint answers
   the package's fixed `400 {"outcome":"csrf_rejected"}`. The frontend reuses
   its existing 401 handling (the login page) for both.
3. **Testing-only plain-HTTP browser login is gone.** The official client
   accepts an explicit loopback HTTP origin (`127.0.0.1` / `[::1]`) only in the
   Development and Testing environments, and every cookie it issues carries
   `Secure`. The `AdminAuthentication:HttpTestOrigins` setting, the
   `LEXARBOR_HOSTED_LOGIN_HTTP_TEST_ORIGINS` mapping and the `HttpTest-`
   cookie prefix are removed; a leftover value stops startup with a fixed
   diagnostic. Private-network HTTP browser logins are no longer supported —
   test deployments use a loopback or HTTPS origin (browsers trust `localhost`).
   The upstream browser-acceptance topology note applies to
   [SignaCore#515](https://github.com/philfanzhou/SignaCore/issues/515).
   *Reversed below by the 0.1.16 upgrade: private-network plain-HTTP browser
   logins are back, zero-configuration.*
4. **Redirect URIs no longer accept a query string.**
   `AdminAuthentication:OidcCode:RedirectUri` and
   `PostLogoutRedirectUri` are validated by the package: absolute URIs with a
   path and without a query, fragment or user info (at 0.1.14 the scheme had to
   be HTTPS, with an explicit loopback exception in Development/Testing; since
   0.1.16 `http` and `https` are equal inputs — see below). A registered static
   query such as `?registered=1` must be removed from the deployment
   configuration.
5. **The default return target of a plain start is `/`.** A
   `GET /admin/auth/start` without a `returnUrl` lands the completed sign-in on
   the application root (the package's fixed default) instead of `/#/books`;
   the allowlist itself is unchanged for explicit values.
6. **Login-binding and logout-return cookies are renamed** to the package's
   derived names (`<session>-login-binding.<state>` and
   `__Secure-<rest>-logout-return`), both one-time and five-minute-lived —
   no compatibility impact.

Further behavioural notes of the package swap:

- The token and prepared-logout backchannels authenticate with HTTP Basic
  (`client_secret_basic`); the client credentials never enter a form body.
- A start whose hosted login is unconfigured still answers its fixed 503
  before the return target is read; a configured but illegal protocol value
  (authority, redirect or post-logout URI, scope) now fails startup instead of
  degrading — the missing-versus-illegal split behind optional login.
- The closed failure reasons keep the repository's four-value vocabulary
  (`canceled`, `denied`, `provider_unavailable`, `sign_in_failed`) and the
  audit rows behind them; the ID-token/access-token validation profile is the
  package's strict default (zero clock skew, scope-echo subset, duplicate JSON
  members rejected, bounded bodies, future `iat` rejected).
- The gated access token's `nbf` is accepted within the package's documented
  30-second fixed skew (`exp` is still exact); see the SignaCore README.

## Plain-HTTP browser logins restored (0.1.16; #209)

The upgrade to
[`SignaCore.Client.AspNetCore` 0.1.16](https://www.nuget.org/packages/SignaCore.Client.AspNetCore)
carries the upstream removal of every code-level HTTPS transport gate
([SignaCore#566](https://github.com/philfanzhou/SignaCore/issues/566)): `http` and
`https` redirect URIs are equal inputs in every environment — no environment
privilege, loopback exception or origin allowlist — and declared change 3 of the
migration above is reversed. Private-network plain-HTTP browser logins work again
with zero configuration; serving public deployments over TLS stays the documented
deployment recommendation (see
[Deployment](Deployment.md#plain-http-deployments)), not a code enforcement.

- The whole cookie set follows the redirect URI's scheme. An `https` redirect is
  byte-for-byte unchanged: the `__Host-Lexarbor.AdminSession` session cookie, the
  `__Secure-` derived names and the `Secure` attribute everywhere, so sessions
  signed in before the upgrade keep authenticating — and keep authenticating
  across a rollback.
- A plain-`http` redirect names the session cookie `Lexarbor.AdminSession` (the
  `__Host-` prefix demands `Secure`, which a plain-HTTP deployment cannot set —
  the package's validator refuses that combination at startup) and issues every
  cookie without `Secure`, the antiforgery cookie included, so the write model
  works unmodified. The one-time binding and logout-return cookies derive from
  the de-prefixed session name (`<session>-login-binding.<state>` and
  `<session>-logout-return`).
- Switching a deployment between the schemes requires signing in once: the two
  profiles' cookie names do not collide, and handles, the protected payload
  format, the database and the Data Protection key ring are untouched.
- `AdminAuthentication:HttpTestOrigins` stays removed — the restored capability
  needs no setting; a leftover value still stops startup with the fixed
  diagnostic, now reworded to state that no replacement configuration exists.
- `IdentityService:RequireHttpsMetadata` is unchanged: it remains this
  repository's own deployment security default for the Bearer metadata channel
  (explicit override, Development/Testing off, Production loopback exemption).
