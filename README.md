# Lexarbor

[![CI](https://github.com/philfanzhou/Lexarbor/actions/workflows/ci.yml/badge.svg)](https://github.com/philfanzhou/Lexarbor/actions/workflows/ci.yml)
[![CodeQL](https://github.com/philfanzhou/Lexarbor/actions/workflows/security.yml/badge.svg)](https://github.com/philfanzhou/Lexarbor/actions/workflows/security.yml)

Lexarbor is a self-hosted vocabulary catalog and quiz service. It combines a .NET 10 API, a Vue 3 administration UI, and SQLite storage in one deployable application. It ships no vocabulary data: a new instance starts with an empty catalog that administrators fill with their own books and words.

## Features

- Manage vocabulary books, entries, meanings, examples, and UK/US phonetics.
- Maintain typed word relations through administrator APIs, with confirmation, conditional edits, and atomic batch creation; see [Vocabulary relations](docs/api/VocabularyRelations.md).
- Import words idempotently with database-backed integrity constraints.
- Generate four-option translation questions from one vocabulary book.
- Browse an enabled book's units and paged entries with a Lexarbor-issued API key.
- Automatically complete missing IPA/POS and expand word forms and derivatives when a text model is configured; new words use the existing submission supplement. See [Vocabulary expansion](docs/api/VocabularyExpansion.md).
- Accept definitions no book has from one authenticated integration client, review them (optionally with AI advice), and store approved ones in a supplementary book; see [Meaning submissions](docs/api/MeaningSubmissions.md).
- Serve the business API and administration UI from one HTTP endpoint.
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

The API listens on `http://localhost:5008`; the Vite development server listens on `http://localhost:5175` and proxies administration routes to it. On first startup Lexarbor creates an empty `src/Lexarbor.Host/data/vocabulary.db`; create a book and add words through the administration UI.

## Run with Docker

```bash
docker build -t lexarbor:latest .
bash scripts/start.sh
```

The container publishes port 5008 and stores `vocabulary.db`, a persistent `appsettings.json`, and the Data Protection root-key file `data-protection-root-key` under `./data` by default. The configuration file is copied from the image defaults on first startup and is never overwritten afterward. `scripts/start.sh` accepts `LEXARBOR_PORT`, `LEXARBOR_DATA_DIR`, `LEXARBOR_IMAGE`, and the authentication variables documented in [Deployment](docs/development/Deployment.md). The container runs as a non-root user, and the script runs it as the user who owns the data directory, so an existing deployment whose files were written by an earlier root container needs `sudo chown -R "$(id -u):$(id -g)" ./data` once — see [Deployment](docs/development/Deployment.md).

Data Protection keys are stored inside the SQLite database as ServiceMantle authenticated envelopes (`service_data_protection_keys`), protected by a root key: inject `LEXARBOR_DATA_PROTECTION_ROOT_KEY` or let the first start create an owner-only `data/data-protection-root-key` file. A wrong root key fails startup. Back up the database and the root key (injected value or file) together, separately from each other; losing the root key only invalidates administrator sessions. The retired plaintext `data/admin-keys` directory is not read and can be deleted after an upgrade. Clear `admin_session` before starting after a historical database restore to prevent revoked sessions from returning. See [key storage and recovery](docs/development/Deployment.md#data-protection-key-storage) and [session storage and recovery](docs/development/Deployment.md#encrypted-administrator-session-storage).

## Audio storage

Optional private S3 audio storage defaults to disabled. Existing deployments need no Audio settings or credentials. The [administrator audio API](docs/api/Audio.md) supports metadata, conditional MP3 uploads, logical deletion and short-lived playback links, including disabled books and words without a book. The [public audio API](docs/api/Audio.md#public-book-authorized-playback) returns short-lived links only for words and meanings in an enabled book. Learning webpages and TTS generation are external responsibilities; see the frontend guide for administrator UI availability. See [audio storage configuration and proxy requirements](docs/development/Deployment.md#optional-audio-object-storage) and [private-bucket integration testing](docs/development/Testing.md#audio-storage-verification).

Textbook recording catalogs, uploads and playback have been removed. Word and meaning pronunciation remain available. Before upgrading a deployment that used recordings, follow the [recording removal and backup steps](docs/development/Deployment.md#textbook-recording-removal).

## Authentication

Business `/api/*` and `/integration/*` routes require a Lexarbor-issued API key and its assigned scope. Manage keys at `/#/settings/api-keys` and send them from the calling service's backend. See [Business API keys](docs/api/BusinessApiKeys.md) and [upgrade steps](docs/development/Deployment.md#business-api-key-upgrade). The ServiceMantle health endpoints (`GET /health/live`, `GET /health/ready`, `GET /health`) remain anonymous. Readiness checks startup migrations and SQLite availability. The administrator login ceiling is disabled by default and can be enabled with `RateLimits:AdminLogin:Enabled=true`. See [Deployment](docs/development/Deployment.md#rate-limits-and-client-addresses). Administration routes require the configured `admin` role from a validated Bearer token or an unexpired encrypted server-side session. The hosted SignaCore login (`GET /admin/auth/start`) is the only administrator sign-in: the browser authenticates on the identity provider's page, and only an opaque handle in the Secure, HttpOnly `__Host-Lexarbor.AdminSession` cookie ever reaches it — administrator passwords, access tokens and ID tokens never pass through Lexarbor's frontend. A Bearer header takes precedence over the session cookie; invalid selected credentials never fall back. Local logout revokes the handle and clears both cookies, and can also return the standard RP-initiated end-session URL so the browser can end the upstream session, while already issued access tokens stay valid until their expiry. Storage failures return a safe error and clear browser cookies, but cannot confirm revocation of a copied handle. See [session authentication and rollback](docs/development/Deployment.md#administrator-session-authentication) for HTTPS, CSRF, expiry and recovery details. The administration UI offers the SignaCore navigation, and its logout completes the upstream sign-out through a top-level navigation to the returned `logoutUrl`. See [hosted login](docs/development/Deployment.md#hosted-authorization-code-login).

[SignaCore](docs/development/Deployment.md#connecting-to-signacore) provides the hosted authorization-code login through a registered Confidential application. The removed password proxy (OAuth2 password grant and the gateway-style JSON adapter) and its JWT cookie are gone; see [ADR-008](docs/adr/ADR-008-servicemantle-first-and-hosted-login.md), the [hosted-login release notes](docs/development/HostedLoginReleaseNotes.md), and [Deployment](docs/development/Deployment.md) for the upgrade steps. For isolated test networks that reach the administration UI directly over plain HTTP on private IP addresses, a Testing-only opt-in exists: `LEXARBOR_HOSTED_LOGIN_HTTP_TEST_ORIGINS` allows exact private-IP HTTP callback origins and issues `HttpTest-` cookies without `Secure`; outside the Testing environment the setting refuses startup, and switching modes requires signing in again. See [private-network HTTP testing](docs/development/Deployment.md#private-network-http-testing-testing-only).

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

GitHub Actions repeats these checks on every pull request and on every push to `main`, tests the built container and its persistent files, and scans the image and source. Container images are built and published to GitHub Container Registry only when a `v*.*.*` tag is pushed. See [Automation](docs/development/Automation.md) for the workflow and release contract.

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
