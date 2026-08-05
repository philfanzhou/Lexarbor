# ADR-001: Pluggable administrator credential providers

- Status: Accepted; default provider amended by ADR-004
- Date: 2026-08-05
- Scope: administrator login and authorization; public `/api/*` routes are unchanged

## Context

Administrator login originally embedded a provider-specific JSON contract across the HTTP client, configuration, dependency injection, and login endpoint. The contract uses `POST /api/auth/token`, a camelCase request body, `X-Admin-AppId` and `X-Admin-AppSecret` headers, and a `success` response envelope. Changes to that external service therefore required edits throughout the Host.

JWT issuers also differ in how they serialize role and name claims. Tests that model only one claim shape can pass while real tokens are rejected.

## Decision

Introduce `IAdminCredentialAuthenticator` as the only seam that knows how username and password credentials are exchanged for an access token:

```text
AuthenticateAsync(username, password) -> { Status, AccessToken?, ExpiresIn? }
```

- `OidcPasswordAuthenticator` implements the OAuth2 resource owner password credentials grant. The token endpoint is explicitly configurable and otherwise comes from the JWT Bearer discovery document.
- `GatewayCredentialAuthenticator` contains the optional JSON/header compatibility contract.
- `AdminAuthentication:Provider` selects one implementation when dependency injection resolves the service; there is no per-request switching.
- ADR-004 makes `Oidc` the standalone product default. `Gateway` remains opt-in.

Everything downstream of credential exchange—token validation, role evaluation, Cookie issuance, CSRF checks, and public error envelopes—is provider-independent.

## Security properties

`AdminCredentialResult` contains no provider-supplied user profile. The username and roles returned to the frontend are always derived from the cryptographically validated JWT.

JWT validation checks issuer, audience, signature, and lifetime. Claim parsing accepts both standard short names (`sub`, `name`, `role`) and the equivalent .NET `ClaimTypes` URIs. Authorization uses `AdminRoleRequirement` and reads the required role from `AdminAuthentication:RequiredRole`.

Provider secrets remain server-side and are never written to the frontend, response body, or logs. A missing provider configuration does not prevent public API startup, but administrator login returns 503.

## Configuration

| Configuration | Purpose |
|---|---|
| `IdentityService:{Authority,Issuer,Audience}` | Defines the trusted JWT issuer |
| `AdminAuthentication:{Provider,RequiredRole,CookieName,CookieSecure}` | Defines Lexarbor login and session behavior |
| `AdminAuthentication:Oidc:{TokenEndpoint,ClientId,ClientSecret,Scope}` | Configures OIDC credential exchange |
| `AdminAuthentication:Gateway:{Authority,TokenPath,AppId,AppSecret}` | Configures the optional gateway contract |

`Gateway:Authority` falls back to `IdentityService:Authority`, allowing the token and JWKS endpoints to use either the same or different origins.

## Alternatives

- A single hard-coded provider was rejected because it couples the product to one identity implementation.
- Trusting profile fields returned beside the access token was rejected because those fields are not independently authenticated.
- Applying a global authorization fallback policy was rejected because the public API, health endpoint, static assets, and login page must remain anonymous.

## Consequences

- Adding another credential protocol requires one new `IAdminCredentialAuthenticator` implementation and configuration binding.
- OIDC deployments must explicitly support the password grant used by the current login form.
- Public API paths, request fields, and response envelopes are unchanged.
