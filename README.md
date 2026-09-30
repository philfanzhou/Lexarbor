# Lexarbor

[![CI](https://github.com/philfanzhou/Lexarbor/actions/workflows/ci.yml/badge.svg)](https://github.com/philfanzhou/Lexarbor/actions/workflows/ci.yml)
[![CodeQL](https://github.com/philfanzhou/Lexarbor/actions/workflows/security.yml/badge.svg)](https://github.com/philfanzhou/Lexarbor/actions/workflows/security.yml)

Lexarbor is a self-hosted vocabulary catalog and quiz service. It combines a .NET 10 API, a Vue 3 administration UI, and SQLite storage in one deployable application. It ships no vocabulary data: a new instance starts with an empty catalog that administrators fill with their own books and words.

## Features

- Manage vocabulary books, entries, meanings, examples, and UK/US phonetics.
- Import words idempotently with database-backed integrity constraints.
- Generate four-option translation questions from one vocabulary book.
- Serve the public API and administration UI from one HTTP endpoint.
- Protect administration routes with an external OIDC identity provider and an administrator role.
- Run as a single container with one persistent data and configuration directory.

## Run locally

Requirements: .NET SDK 10 and Node.js 20 or later.

```bash
dotnet restore Lexarbor.sln
dotnet run --project src/Lexarbor.Host/Lexarbor.Host.csproj
```

In another terminal:

```bash
cd frontend
npm ci
npm run dev
```

The API listens on `http://localhost:5008`; the Vite development server listens on `http://localhost:5175` and proxies both the administration and the public API routes to it. On first startup Lexarbor creates an empty `src/Lexarbor.Host/data/vocabulary.db`; create a book and add words through the administration UI.

## Run with Docker

```bash
docker build -t lexarbor:latest .
bash scripts/start.sh
```

The container publishes port 5008 and stores `vocabulary.db`, a persistent `appsettings.json`, and the Data Protection key ring `admin-keys/` under `./data` by default. The configuration file is copied from the image defaults on first startup and is never overwritten afterward. `scripts/start.sh` accepts `LEXARBOR_PORT`, `LEXARBOR_DATA_DIR`, `LEXARBOR_IMAGE`, and the authentication variables documented in [Deployment](docs/development/Deployment.md). The container runs as a non-root user, and the script runs it as the user who owns the data directory, so an existing deployment whose files were written by an earlier root container needs `sudo chown -R "$(id -u):$(id -g)" ./data` once — see [Deployment](docs/development/Deployment.md).

The key ring is also created under the Host content root’s `data/admin-keys` for local runs. Startup requires valid, writable key storage. On Linux/macOS, the ring directory is restricted to 0700 and its files to 0600. Preserve and confidentially back up the entire ring, including old keys; key XML is not encrypted at rest. See [key storage and recovery](docs/development/Deployment.md#data-protection-key-storage) for permissions, rotation, restore, and rollback. The database also stores encrypted administrator sessions for trusted internal sign-in. Password/Gateway login still issues the existing JWT cookie. Back up DB and ring together, and clear `admin_session` before starting after a historical database restore to prevent revoked sessions from returning. See [session storage and recovery](docs/development/Deployment.md#encrypted-administrator-session-storage).

## Authentication

Public `/api/*` routes and `GET /health` do not require authentication. The anonymous surfaces carry a per-client-address rate limit; see [Deployment](docs/development/Deployment.md) for the ceilings and for the reverse-proxy setting that keeps them per client rather than shared. Administration routes require the configured `admin` role from a validated Bearer/JWT cookie or an unexpired encrypted server-side session. The administration login form exchanges credentials server-side and stores its JWT in an HttpOnly cookie; client secrets are never sent to the browser. Internal session sign-in sends only an opaque handle in the Secure, HttpOnly `__Host-Lexarbor.AdminSession` cookie. A Bearer header takes precedence over this new cookie, which takes precedence over the legacy JWT cookie; invalid selected credentials never fall back. Local logout revokes the new handle and clears both cookies. Storage failures return a safe error and clear browser cookies, but cannot confirm revocation of a copied handle. See [session authentication and rollback](docs/development/Deployment.md#administrator-session-authentication) for HTTPS, CSRF, expiry and recovery details. OIDC Code login is not enabled yet.

OIDC is the default credential provider. The current adapter uses the OAuth2 resource owner password credentials grant, so the configured provider must explicitly support that flow. [SignaCore](docs/development/Deployment.md#connecting-to-signacore) is supported through this adapter. A gateway-style JSON adapter remains available for deployments with an existing token gateway. See [ADR-001](docs/adr/ADR-001-pluggable-admin-authentication.md) and [Deployment](docs/development/Deployment.md).

## Verify

```bash
dotnet build Lexarbor.sln --configuration Release
dotnet test --solution Lexarbor.sln --configuration Release --no-build

cd frontend
npm ci
npm run test:types
npx playwright install chromium
npm run test:e2e
```

GitHub Actions repeats these checks on every pull request and on every push to `main`, tests the built container and its persistent files, scans the image and source, and publishes versioned multi-platform images to GitHub Container Registry when a `v*.*.*` tag is pushed, and a moving `edge` image on every push to `main`. See [Automation](docs/development/Automation.md) for the workflow and release contract.

## Repository layout

```text
├── .github/                  GitHub workflows and collaboration templates
├── docs/                     Architecture, operations, and frontend documentation
├── frontend/                 Vue administration application and browser tests
├── scripts/                  Operator-facing scripts
├── src/Lexarbor.*/           Production .NET projects
├── tests/Lexarbor.*.Tests/   .NET unit and integration tests
├── Directory.Build.props     Shared .NET build settings
├── Directory.Packages.props  Central NuGet package versions
├── Dockerfile                Production container build
└── Lexarbor.sln              Repository-level .NET solution
```

See [Repository layout](docs/development/RepositoryLayout.md) for ownership and placement rules.

## Documentation

- [Documentation index](docs/README.md)
- [Architecture and behavior](docs/overview/SecureSelfContainedServiceDesign.md)
- [Administration frontend](docs/frontend/README.md)
- [Database model](docs/database/README.md)
- [Testing](docs/development/Testing.md)

Contributions should follow [CONTRIBUTING.md](CONTRIBUTING.md). Please report vulnerabilities according to [SECURITY.md](SECURITY.md), not through a public issue.

## License

Lexarbor is released under the [MIT License](LICENSE). Lexarbor does not distribute any vocabulary data. This licence does not grant rights to data that users import into their own instances; users or instance operators are responsible for ensuring that they have the rights required to use that data.
