# Lexarbor

Lexarbor is a self-hosted vocabulary catalog and quiz service. It combines a .NET 8 API, a Vue 3 administration UI, SQLite storage, and a bundled 300-word starter vocabulary in one deployable application.

## Features

- Manage vocabulary books, entries, meanings, examples, and UK/US phonetics.
- Import words idempotently with database-backed integrity constraints.
- Generate four-option translation questions from one vocabulary book.
- Serve the public API and administration UI from one HTTP endpoint.
- Protect administration routes with an external OIDC identity provider and an administrator role.
- Run as a single container with one persistent SQLite file.

## Run locally

Requirements: .NET SDK 8 and Node.js 20 or later.

```bash
dotnet restore src/Lexarbor.sln
dotnet run --project src/Host/Lexarbor.Host.csproj
```

In another terminal:

```bash
cd frontend
npm ci
npm run dev
```

The API listens on `http://localhost:5008`; the Vite development server listens on `http://localhost:5175` and proxies administration requests to the API. On first startup Lexarbor creates `src/Host/data/vocabulary.db` and imports the bundled starter book.

## Run with Docker

```bash
docker build -f src/Host/Dockerfile -t lexarbor:latest .
bash start.sh
```

The container publishes port 5008 and stores the database under `./data` by default. `start.sh` accepts `LEXARBOR_PORT`, `LEXARBOR_DATA_DIR`, `LEXARBOR_IMAGE`, and the authentication variables documented in [Deployment](docs/development/Deployment.md).

## Authentication

Public `/api/*` routes and `GET /health` do not require authentication. Administration routes require a validated JWT containing the configured `admin` role. The administration login form exchanges credentials server-side, so access tokens and client secrets are never exposed to the browser.

OIDC is the default credential provider. The current adapter uses the OAuth2 resource owner password credentials grant, so the configured provider must explicitly support that flow. A gateway-style JSON adapter remains available for deployments with an existing token gateway. See [ADR-001](docs/adr/ADR-001-pluggable-admin-authentication.md) and [Deployment](docs/development/Deployment.md).

## Verify

```bash
dotnet build src/Lexarbor.sln --configuration Release
dotnet test src/Lexarbor.sln --configuration Release --no-build

cd frontend
npm ci
npm run test:types
npm run build
```

## Documentation

- [Documentation index](docs/README.md)
- [Architecture and behavior](docs/overview/SecureSelfContainedServiceDesign.md)
- [Database model](docs/database/README.md)
- [Testing](docs/development/Testing.md)

## License

Lexarbor, including its bundled starter vocabulary, is released under the [MIT License](LICENSE). Third-party vocabulary datasets added in the future may have separate licenses and must retain their own notices.
