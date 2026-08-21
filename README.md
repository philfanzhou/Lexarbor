# Lexarbor

[![CI](https://github.com/philfanzhou/Lexarbor/actions/workflows/ci.yml/badge.svg)](https://github.com/philfanzhou/Lexarbor/actions/workflows/ci.yml)
[![CodeQL](https://github.com/philfanzhou/Lexarbor/actions/workflows/security.yml/badge.svg)](https://github.com/philfanzhou/Lexarbor/actions/workflows/security.yml)

Lexarbor is a self-hosted vocabulary catalog and quiz service. It combines a .NET 10 API, a Vue 3 administration UI, SQLite storage, and a bundled 300-word starter vocabulary in one deployable application.

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

The API listens on `http://localhost:5008`; the Vite development server listens on `http://localhost:5175` and proxies both the administration and the public API routes to it. On first startup Lexarbor creates `src/Lexarbor.Host/data/vocabulary.db` and imports the bundled starter book.

## Run with Docker

```bash
docker build -t lexarbor:latest .
bash scripts/start.sh
```

The container publishes port 5008 and stores both `vocabulary.db` and a persistent `appsettings.json` under `./data` by default. The configuration file is copied from the image defaults on first startup and is never overwritten afterward. `scripts/start.sh` accepts `LEXARBOR_PORT`, `LEXARBOR_DATA_DIR`, `LEXARBOR_IMAGE`, and the authentication variables documented in [Deployment](docs/development/Deployment.md). The container runs as a non-root user, and the script runs it as the user who owns the data directory, so an existing deployment whose files were written by an earlier root container needs `sudo chown -R "$(id -u):$(id -g)" ./data` once — see [Deployment](docs/development/Deployment.md).

## Authentication

Public `/api/*` routes and `GET /health` do not require authentication. The anonymous surfaces carry a per-client-address rate limit; see [Deployment](docs/development/Deployment.md) for the ceilings and for the reverse-proxy setting that keeps them per client rather than shared. Administration routes require a validated JWT containing the configured `admin` role. The administration login form exchanges credentials server-side, so access tokens and client secrets are never exposed to the browser.

OIDC is the default credential provider. The current adapter uses the OAuth2 resource owner password credentials grant, so the configured provider must explicitly support that flow. A gateway-style JSON adapter remains available for deployments with an existing token gateway. See [ADR-001](docs/adr/ADR-001-pluggable-admin-authentication.md) and [Deployment](docs/development/Deployment.md).

## Verify

```bash
dotnet build Lexarbor.sln --configuration Release
dotnet test Lexarbor.sln --configuration Release --no-build

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

Lexarbor, including its bundled starter vocabulary, is released under the [MIT License](LICENSE). Third-party vocabulary datasets added in the future may have separate licenses and must retain their own notices.
