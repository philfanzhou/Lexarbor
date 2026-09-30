# Hosted login release notes

## Breaking changes

Selecting the new optional `OidcCode` provider disables `POST /admin/auth/login`
before password JSON binding, returning 400 with `Password login is disabled for
hosted authentication.` Existing cookie CSRF rules can still return 403 first.
The default `Oidc` and optional `Gateway` modes retain their previous contracts.

Successful Code login now creates an encrypted server-side session and sends only
an opaque Secure, HttpOnly cookie, bounded by the verified access token expiry.
It revokes the presented old opaque session atomically and deletes the legacy JWT
cookie only after confirmed commit. Failures before commit preserve existing
sessions; lost commit/response results remain unknown and are never retried.

New public `method`, `start`, and `callback` routes support backend hosted login.
The frontend still uses its password form; UI rollout is a separate change.
Deployment registration, independent Code configuration, proxy query log
suppression and rollback are documented in
[Deployment](Deployment.md#hosted-authorization-code-login-optional).

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
[Deployment](Deployment.md#prepared-upstream-logout). The frontend does not
navigate to `logoutUrl` or render the new reasons until the separate UI rollout
(#155).
