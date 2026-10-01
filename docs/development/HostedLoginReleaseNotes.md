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
  429/`Retry-After` contract.
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
