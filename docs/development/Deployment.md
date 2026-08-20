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

Because `latest` and the major tags are repointed by later releases, ask the running container which version it actually is rather than relying on the tag it was pulled with. The version is written once at startup:

```bash
docker logs lexarbor 2>&1 | grep "Lexarbor starting"
```

```text
info: Lexarbor starting, version 1.2.3
```

It is deliberately not served over HTTP. The only endpoint that could carry it is `/health`, which is anonymous so that the container probe can reach it without credentials, and telling an unauthenticated caller which release it is talking to tells it which published issues to try. Reading the version from the log requires access to the host running the container.

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

### Container user and file ownership

The container runs as an unprivileged user. Nothing it does needs root: it
listens on 5008, which is outside the privileged range, and writes only under
`/app/data`.

How that interacts with the data directory depends on how it is mounted:

| Mount | Who owns `/app/data` | What runs the container |
|---|---|---|
| Host bind mount (what `scripts/start.sh` does) | The host user who owns the directory | `scripts/start.sh` passes `--user "$(id -u):$(id -g)"` |
| Named or anonymous volume | The image's own unprivileged user | The image's default user, no `--user` needed |

A bind mount keeps the host's ownership, so the container must run as the user
who owns that directory — Docker cannot grant a container user rights to a host
directory it does not own. This also means the database and configuration files
are now owned by whoever runs the script, so backing them up no longer needs
`sudo`.

**Upgrading an existing deployment.** A container from an earlier image ran as
root and left `data/vocabulary.db` and `data/appsettings.json` owned by root.
After upgrading, take ownership once before starting:

```bash
sudo chown -R "$(id -u):$(id -g)" ./data
bash scripts/start.sh
```

Skipping this leaves the application unable to open its own database, and it
exits at startup with a permission error rather than starting in a degraded
state. Deployments using a named or anonymous volume rather than a bind mount
need no action.

### Health status

The image declares a `HEALTHCHECK`, so `docker ps` reports the application's own
readiness rather than only whether the process is alive:

```bash
docker ps --format '{{.Names}} {{.Status}}'
```

```text
lexarbor   Up 2 minutes (healthy)
```

The probe runs the published assembly with `--health-check`, which requests
`/health` on loopback and exits non-zero when it does not answer. It is not a
`curl` call, because the runtime image ships no HTTP client and adding one would
give any future remote-code-execution a download tool the image currently lacks.

The first check is deferred by 30 seconds, which covers migrations and the
300-word seed on a first start. Note that Docker reports an unhealthy container
but does not restart it; `--restart unless-stopped` acts on the process exiting,
not on the health status.

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

### Write-ahead logging

Lexarbor switches the database to WAL journalling on every startup. Under the default rollback journal a reader blocks a writer, so the anonymous detail and question endpoints contended with every administrative write; WAL removes that. The setting is stored in the database header, so it applies to an existing database on its next start and needs no migration.

Two consequences for operators:

- **`/app/data` must be a local filesystem.** WAL places a shared-memory file beside the database, which some network filesystems do not support. A bind mount from the host or a Docker volume is fine; an NFS or SMB mount is not.
- **The database is three files, not one.** `vocabulary.db` is accompanied by `vocabulary.db-wal` and `vocabulary.db-shm` while the application runs. A file copy that takes only `vocabulary.db` can miss recently committed data. Copy all three with the application stopped, or use a SQLite online-backup tool, which handles this correctly on its own.

`Default Timeout` in the connection string bounds how long a write waits for a database another connection is holding. Lexarbor lowers the driver's 30-second default to 5 seconds, so contention answers `503` with a `Retry-After` header instead of occupying a request thread for longer than the caller is prepared to wait. Set `Default Timeout=` explicitly in `ConnectionStrings:Default` to choose a different value.

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

## Rate limits and client addresses

The two anonymous surfaces carry a per-client-address ceiling. `POST /admin/auth/login`
forwards credentials to the identity provider, so without one it is a password-guessing
oracle and a way to aim traffic at that provider from an address the provider attributes
to Lexarbor. The `/api/*` routes are metered far more loosely, only to stop one caller
monopolising a single-instance SQLite deployment. Administration routes are not limited:
an authenticated administrator is not the threat, and metering the administration UI
would break it long before it broke an attacker.

A refused request answers `429` in the standard envelope with a `Retry-After` header.

| Configuration key | Default | Purpose |
|---|---|---|
| `RateLimits:AdminLogin:PermitLimit` | `10` | Login attempts per window, per client address |
| `RateLimits:AdminLogin:WindowSeconds` | `300` | Login window length |
| `RateLimits:AdminLogin:Enabled` | `true` | Set to `false` to remove the login ceiling |
| `RateLimits:PublicApi:PermitLimit` | `300` | Anonymous `/api/*` requests per window, per client address |
| `RateLimits:PublicApi:WindowSeconds` | `60` | Public API window length |
| `RateLimits:PublicApi:Enabled` | `true` | Set to `false` to remove the public API ceiling |

A permit count or window below 1 fails startup rather than being clamped, so a typo in a
ceiling cannot become a value that looks as though it took effect. Disabling a limit is
therefore an explicit `Enabled: false`, and startup logs a warning naming the policy.

### Behind a reverse proxy

The ceilings partition on the address the connection reports. Behind a reverse proxy that
is the proxy for every request, which turns a per-client limit into a shared one — and a
shared login limit is itself a way to lock the administrator out. Name the hops to fix it:

| Configuration key | Default | Purpose |
|---|---|---|
| `Network:TrustedProxies` | empty | Proxy addresses allowed to set `X-Forwarded-For`, for example `172.18.0.2` |
| `Network:TrustedNetworks` | empty | Proxy ranges in CIDR form, for example `172.18.0.0/16` |
| `Network:ForwardLimit` | `1` | Trusted hops in front of Lexarbor |

Nothing is trusted until one of these is set, and forwarded headers are ignored entirely
until then. That default is deliberate: `X-Forwarded-For` is client-supplied, and honouring
it from an unknown source would let any caller mint a fresh partition key per request and
pass the ceiling without ever reaching it. A degraded shared limit is a visible operational
problem; a bypassable limit is an invisible security one. Set `ForwardLimit` to the real
number of trusted hops — raising it further hands the extra steps back to the client, whose
own header content occupies the left of the list.

Startup logs both ceilings and whether any hop is trusted, so a misconfigured proxy is
visible in the first lines of the container log.

## Health and smoke checks

```bash
curl http://localhost:5008/health
curl -i http://localhost:5008/admin/vocabulary-books
curl http://localhost:5008/api/vocabulary-books/all
```

Expected results: health returns 200; an anonymous administration request returns 401; the public book request returns a success envelope containing `Starter English 300` after first startup.
