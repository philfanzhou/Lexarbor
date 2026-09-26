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
docker logs lexarbor 2>&1 | grep -E "Lexarbor starting|Lexarbor build"
```

```text
info: Lexarbor starting, version 1.2.3
info: Lexarbor build, channel release, revision 0123456789abcdef0123456789abcdef01234567
```

Build identity is currently available only in startup logs, not over HTTP. Anonymous `/health` remains exactly `{"success":true,"data":{"status":"healthy"}}`. Reading the identity requires access to the container logs.

| Docker build argument | MSBuild property | Local default |
|---|---|---|
| `APP_VERSION` | `Version` | `0.0.0-dev` |
| `APP_REVISION` | `BuildRevision` | empty (reported as `unknown` in logs) |
| `APP_CHANNEL` | `BuildChannel` | `development` |

The release workflow provides the tag version (including prerelease suffixes), the checked-out commit, and `release`. Main builds keep `0.0.0-dev` but explicitly supply `edge` and their commit. Ordinary `dotnet` and Docker builds use `development` even when a local Git checkout exists. Explicit rebuilds can provide the three build arguments, or the corresponding `dotnet publish -p:Version=... -p:BuildRevision=... -p:BuildChannel=...` properties.

The Host reads its compiled assembly only: no runtime environment variable, persisted configuration, `.git`, Docker socket, or registry lookup is involved. Revision comes from its own metadata, never from the SDK's `+` suffix. Missing/blank versions become `unknown`; missing, duplicate, or invalid revisions become null (`unknown` in logs). Valid revisions are exactly 40 hexadecimal characters and normalize to lowercase. Missing, duplicate, or unrecognized channels fall back to `development`; the only accepted values are `release`, `edge`, and `development`. Each field falls back independently, and missing metadata does not prevent startup.

Version and revision describe the application build, not the image digest. Moving `latest` and `edge` tags can change, and rebuilding one commit may produce a different image. Build inputs are declarations by the builder, not proof of authenticity or uncommitted changes; deploy trusted images. No version state is written to `/app/data`.

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

The first check is deferred by 30 seconds, which covers migrations on a first
start. Note that Docker reports an unhealthy container but does not restart it;
`--restart unless-stopped` acts on the process exiting, not on the health status.

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
| `Database:InitializeOnStartup` | `true` | Create a missing database and apply migrations |

On first startup Lexarbor creates an empty database: it ships no vocabulary data, so administrators create books and add words themselves. Existing databases are migrated only; their rows are neither added to nor removed, so a database created by an earlier release keeps its `Starter English 300` book. Rolling back to such an earlier image does not reload that book into a database this release created, because the earlier release also only migrates an existing file. Stop writes before copying the database, or use a SQLite online-backup tool.

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
| `LEXARBOR_REQUIRE_HTTPS_METADATA` | required outside Development | `IdentityService:RequireHttpsMetadata` |
| `LEXARBOR_ADMIN_AUTH_PROVIDER` | not supplied | `AdminAuthentication:Provider` |
| `LEXARBOR_OIDC_TOKEN_ENDPOINT` | not supplied | `AdminAuthentication:Oidc:TokenEndpoint` |
| `LEXARBOR_OIDC_CLIENT_ID` | not supplied | `AdminAuthentication:Oidc:ClientId` |
| `LEXARBOR_OIDC_CLIENT_SECRET` | not supplied | `AdminAuthentication:Oidc:ClientSecret` |
| `LEXARBOR_OIDC_SCOPE` | not supplied | `AdminAuthentication:Oidc:Scope` |
| `LEXARBOR_COOKIE_SECURE` | not supplied | `AdminAuthentication:CookieSecure` |

When these variables are not supplied, values come from the persistent file and ultimately from the image defaults. The validated token must contain `role=admin` by default. Override `AdminAuthentication__RequiredRole` to use another role. Set `LEXARBOR_COOKIE_SECURE=true` whenever the browser accesses Lexarbor over HTTPS. Missing credential-provider settings do not prevent startup; administration login returns 503 until configured.

A provider refusal that concerns the client rather than the password — RFC 6749 `invalid_client`, `unauthorized_client`, `unsupported_grant_type`, `invalid_scope`, `invalid_request`, `server_error`, or `temporarily_unavailable` — answers 502 rather than 401, and the log names the error code. `invalid_grant` and any other code answer 401.

`LEXARBOR_REQUIRE_HTTPS_METADATA` decides whether the provider's signing metadata may be fetched over plain HTTP. It is required unless the environment is Development or the authority is a loopback address, so an `http://` authority pointing at another host stops the container at startup with a message naming the setting, rather than starting and answering 500 on every administration request. Loopback is exempt because there is no network path to rewrite, and because the image's placeholder authority is a loopback one: a container that has not been given an identity provider still starts and serves its public API. The keys served from that address decide every administration authorization, so anyone able to rewrite the response can mint an administrator token; setting this to `false` is a statement that the network path to the provider is trusted. It is deliberately absent from the image's `appsettings.json`: writing a value there would freeze it into the persistent file on first start and take the environment out of the decision. Whichever way it resolves, the startup log says so — at information when metadata is required and at warning when it is not.

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

### Connecting to SignaCore

[SignaCore](https://github.com/philfanzhou/SignaCore) works with the default `Oidc` provider through its RFC 6749 token endpoint, `/oauth2/token`. SignaCore has no authorization endpoint, so administrators still sign in through the Lexarbor login form and Lexarbor performs the password grant server-side; the browser never receives the client secret or the access token.

In SignaCore:

1. Register an application for Lexarbor in the administration console, for example with AppId `lexarbor-admin`, and keep its AppSecret.
2. Set the application's access-token audience mode to per-application (`PUT /api/admin/apps/{appId}/audience-mode`). Its tokens then carry `aud` equal to the AppId, and tokens issued to other applications are rejected by Lexarbor. In the shared mode every token carries SignaCore's `Jwt:Audience` (default `SignaCore.Services`), which any other shared-mode service would also accept.
3. Decide who administers Lexarbor. Lexarbor requires `role=admin` in the token. SignaCore adds that role for its bootstrap administrator (`Admin:Username`) in every application; any other account receives roles only from the application's claims callback, whose response lists them in `roles`.

In Lexarbor:

```bash
export LEXARBOR_IDENTITY_AUTHORITY=https://signacore.example.com
export LEXARBOR_IDENTITY_ISSUER=https://signacore.example.com
export LEXARBOR_IDENTITY_AUDIENCE=lexarbor-admin
export LEXARBOR_OIDC_CLIENT_ID=lexarbor-admin
export LEXARBOR_OIDC_CLIENT_SECRET=replace-me
export LEXARBOR_OIDC_SCOPE=
export LEXARBOR_COOKIE_SECURE=true
bash scripts/start.sh
```

- `LEXARBOR_OIDC_SCOPE` must be set, and set to an empty value. SignaCore rejects every requested scope with `invalid_scope`, including the image default `openid profile`. A deployment configured through the persistent `appsettings.json` instead sets `AdminAuthentication:Oidc:Scope` to `""` there.
- `LEXARBOR_IDENTITY_ISSUER` must equal the `issuer` field of SignaCore's `/.well-known/openid-configuration` exactly. `LEXARBOR_OIDC_TOKEN_ENDPOINT` is not needed, because the token endpoint is read from the same document.
- `LEXARBOR_IDENTITY_AUDIENCE` and `LEXARBOR_OIDC_CLIENT_ID` are both the AppId, and `LEXARBOR_OIDC_CLIENT_SECRET` is the AppSecret.
- A session lasts as long as SignaCore's access token (`Jwt:TokenExpirationHours`, 2 hours by default). Lexarbor discards the refresh token, so the administrator signs in again when the session ends. Disabling an account or removing its role in SignaCore does not end a Lexarbor session that has already started; it ends when the token expires.

When login answers 502, the Lexarbor log names the cause. `rejected with invalid_scope` means the scope is still being sent, `rejected with invalid_client` means the client ID or secret is wrong, and `Identity access token validation failed` usually means the issuer or audience does not match the token.

The `Gateway` adapter also speaks SignaCore's older `/api/auth/token` contract, but new deployments should use `/oauth2/token` through the `Oidc` provider.

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

Expected results: health returns 200; an anonymous administration request returns 401; the public book request returns a success envelope whose `data.books` is empty on a new instance.

Administrator vocabulary GET routes include disabled-book and unassigned vocabulary for maintenance. These are read-only deferred SQLite snapshots; no configuration, schema migration, startup cleanup, or persistence-directory change is required. Rolling back the API removes these new reads without changing stored data. Back up the SQLite database consistently before any separate editing or cleanup operation; read requests themselves do not alter it.
