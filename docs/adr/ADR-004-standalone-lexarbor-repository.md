# ADR-004: Extract the vocabulary service as Lexarbor

- Status: Accepted
- Date: 2026-08-05

## Context

The vocabulary service had become operationally self-contained: it used SQLite, bundled its frontend and starter data, and had no compile-time dependency on the original monorepo's shared projects. Keeping it inside the monorepo still imposed repository-specific paths, names, build scripts, and a default identity integration on a product intended for general reuse.

## Decision

- Extract the service subtree into the standalone `Lexarbor` repository while preserving its service-specific Git history.
- Rename .NET assemblies and namespaces from the monorepo-qualified name to `Lexarbor.*`.
- Keep the existing public and administration HTTP paths and the SQLite schema unchanged.
- Use OIDC as the default external administrator credential provider. Keep the former JSON/header token contract as an optional, generically named `Gateway` adapter.
- Build Docker images from the standalone repository root and provide repository-local GitHub Actions and Jenkins workflows.
- Distribute the project and the original bundled starter vocabulary under the MIT License.

## Consequences

- Existing source consumers must update assembly and namespace names.
- Existing deployments must rename container settings and environment variables to the `LEXARBOR_*` names.
- Existing SQLite files remain usable because table names, columns, migration identifiers, and business identifiers are unchanged.
- OIDC deployments must use an identity provider that supports the password grant used by the current login UI.
- Future third-party vocabulary datasets must retain their own licenses and notices; their inclusion is not implied by the repository's MIT License.
