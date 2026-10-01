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
- `GET /admin/auth/method` now always returns `{"success":true,"data":{"method":"hosted"}}`.
- `GET /admin/auth/start` keeps its per-IP `admin-login` rate limit and its
  429/`Retry-After` contract.

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

New public `method`, `start`, and `callback` routes support backend hosted login.
The administration UI rollout (#155) switched the frontend to them; see
[Administration UI rollout](#administration-ui-rollout).
Deployment registration, independent Code configuration, proxy query log
suppression and rollback are documented in
[Deployment](Deployment.md#hosted-authorization-code-login).

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

- The login page reads `GET /admin/auth/method` before offering any action. In
  `hosted` mode it renders only a SignaCore navigation and no password field; in
  `password` mode it keeps the existing credential form unchanged. A failed or
  malformed method read offers no sign-in action at all and can be retried, so a
  password form can never flash or submit before the mode is known.
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
  navigation overrides it. When absent, the local session has still ended; in
  hosted mode the UI then says the provider may still hold a session and never
  claims SignaCore signed out. Old-mode logout keeps its previous silent
  behaviour.

The default `Oidc` and `Gateway` deployments see no change: the method reads
`password` and the existing form, logout, and session restore behave exactly as
before. Deployment switching, legacy-cookie expiry, and rollback are documented in
[Deployment](Deployment.md#hosted-authorization-code-login-optional).
