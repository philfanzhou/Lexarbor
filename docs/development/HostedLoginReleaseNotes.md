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
The frontend still uses its password form; UI rollout and prepared upstream logout
are separate changes. Deployment registration, independent Code configuration,
proxy query log suppression and rollback are documented in [Deployment](Deployment.md#hosted-authorization-code-login-optional).
