# Deployment and operations

## Build the image

The root of the repository is the Docker build context:

```bash
docker build -t lexarbor:latest .
```

The multi-stage build compiles the Vue frontend, publishes the .NET backend, and copies the static files into the Host output. The runtime image exposes HTTP port 5008 and stores durable state in `/app/data`:

- `/app/data/vocabulary.db` contains the SQLite database.
- `/app/data/appsettings.json` contains the operator-managed application configuration.

Tagged releases publish SBOM-enabled images with build provenance for AMD64 and ARM64 to `ghcr.io/philfanzhou/lexarbor`. For example:

```bash
docker pull ghcr.io/philfanzhou/lexarbor:1.2.3
```

Stable releases also update the `latest`, major, and major/minor tags. Pre-releases such as `v1.2.3-rc.1` publish only their full version tag.

Pushes to `main` publish `edge`, which always points at the newest development build and never at a release:

```bash
docker pull ghcr.io/philfanzhou/lexarbor:edge
```

It passes the same CI the release images do, but it is unversioned and moves without notice, so it suits trying an unreleased change rather than running a deployment.

Because `latest` and the major tags are repointed by later releases, ask the running container which version it actually is rather than relying on the tag it was pulled with:

```bash
curl --silent http://127.0.0.1:5008/health
```

```json
{ "success": true, "data": { "status": "healthy", "version": "1.2.3" } }
```

An image built from a local `docker build` reports `0.0.0-dev` unless `--build-arg APP_VERSION=` is given.

## Start the container

```bash
bash scripts/start.sh
```

`scripts/start.sh` creates a `lexarbor-net` Docker network, replaces an existing container with the same name, mounts one data directory, and starts the image. No separate configuration-file mount is required. Its general settings are:

| Environment variable | Default | Purpose |
|---|---|---|
| `LEXARBOR_IMAGE` | `lexarbor:latest` | Image to run |
| `LEXARBOR_CONTAINER_NAME` | `lexarbor` | Container name |
| `LEXARBOR_NETWORK` | `lexarbor-net` | Docker network |
| `LEXARBOR_PORT` | `5008` | Host port mapped to container port 5008 |
| `LEXARBOR_DATA_DIR` | `<repository>/data` | Host directory mounted at `/app/data` |

The deployment is single-instance only. Do not mount the same SQLite file into multiple running containers.

On the first container startup, Lexarbor copies the image's built-in `appsettings.json` to `/app/data/appsettings.json`. If that file already exists, Lexarbor loads it without modifying it. Configuration precedence is:

1. image defaults;
2. `/app/data/appsettings.json`;
3. explicitly supplied environment variables;
4. command-line arguments.

Therefore the persistent file controls normal deployments, while an explicit environment variable remains available for secret injection or an emergency override. When `/app/data` is not bound to a host directory or named volume, Docker's image-declared anonymous volume still lets the application run, but a newly created container will not automatically reuse that data.

## Database

| Configuration key | Default | Purpose |
|---|---|---|
| `ConnectionStrings:Default` | `Data Source=data/vocabulary.db` | SQLite data source; relative paths use the Host content root |
| `Database:InitializeOnStartup` | `true` | Apply migrations and seed a newly created database |

On first startup Lexarbor imports the bundled 300-word starter book in one transaction. Existing databases are migrated but are not seeded again. Stop writes before copying the database, or use a SQLite online-backup tool.

## OIDC authentication (default)

The administration UI submits a username and password to Lexarbor. The backend exchanges those credentials with the configured OIDC token endpoint, validates the returned JWT, requires the configured role, and stores the access token in an HttpOnly cookie. The current adapter uses the OAuth2 resource owner password credentials grant; the identity provider must explicitly enable it.

| Environment variable | Default | .NET configuration key |
|---|---|---|
| `LEXARBOR_IDENTITY_AUTHORITY` | not supplied | `IdentityService:Authority` |
| `LEXARBOR_IDENTITY_ISSUER` | Authority when Authority is explicitly supplied | `IdentityService:Issuer` |
| `LEXARBOR_IDENTITY_AUDIENCE` | not supplied | `IdentityService:Audience` |
| `LEXARBOR_ADMIN_AUTH_PROVIDER` | not supplied | `AdminAuthentication:Provider` |
| `LEXARBOR_OIDC_TOKEN_ENDPOINT` | not supplied | `AdminAuthentication:Oidc:TokenEndpoint` |
| `LEXARBOR_OIDC_CLIENT_ID` | not supplied | `AdminAuthentication:Oidc:ClientId` |
| `LEXARBOR_OIDC_CLIENT_SECRET` | not supplied | `AdminAuthentication:Oidc:ClientSecret` |
| `LEXARBOR_OIDC_SCOPE` | not supplied | `AdminAuthentication:Oidc:Scope` |
| `LEXARBOR_COOKIE_SECURE` | not supplied | `AdminAuthentication:CookieSecure` |

When these variables are not supplied, values come from the persistent file and ultimately from the image defaults. The validated token must contain `role=admin` by default. Override `AdminAuthentication__RequiredRole` to use another role. Set `LEXARBOR_COOKIE_SECURE=true` whenever the browser accesses Lexarbor over HTTPS. Missing credential-provider settings do not prevent startup; administration login returns 503 until configured.

Example:

```bash
export LEXARBOR_IDENTITY_AUTHORITY=https://identity.example.com
export LEXARBOR_IDENTITY_ISSUER=https://identity.example.com
export LEXARBOR_IDENTITY_AUDIENCE=lexarbor
export LEXARBOR_OIDC_CLIENT_ID=lexarbor-admin
export LEXARBOR_OIDC_CLIENT_SECRET=replace-me
export LEXARBOR_COOKIE_SECURE=true
bash scripts/start.sh
```

## Gateway adapter (optional)

Set `LEXARBOR_ADMIN_AUTH_PROVIDER=Gateway` to use the compatibility adapter for a JSON password-token endpoint. It sends `X-Admin-AppId` and `X-Admin-AppSecret` headers and expects a success envelope containing an access token and user information.

| Environment variable | Purpose |
|---|---|
| `LEXARBOR_GATEWAY_AUTHORITY` | Optional login base URL; falls back to Identity Authority |
| `LEXARBOR_GATEWAY_TOKEN_PATH` | Token path, default `/api/auth/token` |
| `LEXARBOR_GATEWAY_APP_ID` | Application identifier |
| `LEXARBOR_GATEWAY_APP_SECRET` | Application secret |

## Health and smoke checks

```bash
curl http://localhost:5008/health
curl -i http://localhost:5008/admin/vocabulary-books
curl http://localhost:5008/api/vocabulary-books/all
```

Expected results: health returns 200; an anonymous administration request returns 401; the public book request returns a success envelope containing `Starter English 300` after first startup.
