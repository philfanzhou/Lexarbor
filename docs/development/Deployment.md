# Deployment and operations

## Build the image

The root of the repository is the Docker build context:

```bash
docker build -t lexarbor:latest .
```

The multi-stage build compiles the Vue frontend, publishes the .NET backend, and copies the static files into the Host output. The runtime image exposes HTTP port 5008 and stores durable state in `/app/data`:

- `/app/data/vocabulary.db` contains the SQLite database.
- `/app/data/appsettings.json` contains the operator-managed application configuration.
- `/app/data/admin-keys/` contains the persistent ASP.NET Core Data Protection key ring.

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

Authorized administrators can also read the running build through `GET /admin/system/version` using the existing Cookie or Bearer credentials. The response is `{"success":true,"data":{"version":"1.2.3","revision":null,"channel":"release"}}`; revision is the full SHA when supplied at build time. All responses on this path, including 401/403, use `Cache-Control: no-store`, without ETag, Last-Modified, or 304 responses. Anonymous `/health` remains exactly `{"success":true,"data":{"status":"healthy"}}`. Anonymous callers receive no build identity.

The signed-in administration header shows the same identity beside the `Lexarbor` brand — `v1.2.3` (prerelease suffixes preserved), `edge · a1b2c3d`, or 开发版本 — with the full version, complete revision, and channel behind a keyboard-reachable detail opened from the label. A failed or unreadable fetch reads 版本未知 and does not block the page; the frontend asks once per administrator session and again after a browser reload, and never caches the values in web storage.

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

On first startup Lexarbor creates an empty database: it ships no vocabulary data, so administrators create books and add words themselves. Existing databases are migrated only; their rows are neither added to nor removed, so a database created by an earlier release keeps its `Starter English 300` book. The same holds for the schema migration that adds book units and meaning-to-unit assignments: it creates two empty tables and one index and rewrites no existing row, and words or meanings work exactly as before without any unit. Rolling back to such an earlier image does not reload that book into a database this release created, because the earlier release also only migrates an existing file. Stop writes before copying the database, or use a SQLite online-backup tool.

### Write-ahead logging

Lexarbor switches the database to WAL journalling on every startup. Under the default rollback journal a reader blocks a writer, so the anonymous detail and question endpoints contended with every administrative write; WAL removes that. The setting is stored in the database header, so it applies to an existing database on its next start and needs no migration.

Two consequences for operators:

- **`/app/data` must be a local filesystem.** WAL places a shared-memory file beside the database, which some network filesystems do not support. A bind mount from the host or a Docker volume is fine; an NFS or SMB mount is not.
- **The database is three files, not one.** `vocabulary.db` is accompanied by `vocabulary.db-wal` and `vocabulary.db-shm` while the application runs. A file copy that takes only `vocabulary.db` can miss recently committed data. Copy all three with the application stopped, or use a SQLite online-backup tool, which handles this correctly on its own.

`Default Timeout` in the connection string bounds how long a write waits for a database another connection is holding. Lexarbor lowers the driver's 30-second default to 5 seconds, so contention answers `503` with a `Retry-After` header instead of occupying a request thread for longer than the caller is prepared to wait. Set `Default Timeout=` explicitly in `ConnectionStrings:Default` to choose a different value.

## Data Protection key storage

The Host uses `data/admin-keys` relative to its content root (`/app/data/admin-keys`
in the container), with the fixed application name `Lexarbor`. No extra volume or
configuration setting is needed. The ring protects administrator
sessions under the independent versioned purpose `Lexarbor.AdminSession.v1`.
Password login still issues the legacy JWT cookie; the internal session scheme
described below also uses this ring. Anonymous APIs and health responses are unchanged.

On Linux and macOS, startup creates or restricts the ring directory to 0700 and its
key XML files to 0600. Only the runtime user can read, write, and traverse the directory.
Permission changes stay inside this ring; other `data` files and directories are
not recursively changed. The framework also creates new keys with no group/other
permissions. A symbolic link for the ring or a ring file is rejected. On Windows,
operators must protect the directory with an ACL granting access only to the runtime
user; Lexarbor does not automatically configure an equivalent Windows ACL. Protect
the parent `data` directory's ownership and ACL as well.

Before serving requests, startup verifies read/write access, loads every retained
key, and performs a non-sensitive Protect/Unprotect probe using a separate purpose.
An inaccessible or read-only filesystem, invalid key XML, or unusable retained key
stops startup with a safe diagnostic. Lexarbor does not fall back to an in-memory
ring, delete damaged keys, or log XML/protected payloads. Correct ownership and
permissions or restore a complete valid ring before restarting.

ASP.NET Core retains automatic key rotation (the default lifetime is 90 days).
Expired keys must remain available to decrypt older payloads. Application upgrades
and container recreation reuse the mounted ring without replacing keys. Do not
remove old keys to force rotation. An anonymous Docker volume must be explicitly
reused if recreating a container; a fresh volume cannot decrypt old payloads.

**Security boundary.** File persistence stores unencrypted key XML: filesystem
permissions provide access control, not disk-theft protection. Anyone with runtime
user/root privileges or a complete data backup can use these keys. This contract
covers one instance on a trusted local filesystem; it does not protect against an
attacker controlling parent directories or replacing files at runtime, and does not
support network mounts or multiple instances. No external KMS or certificate-based
key encryption is configured. See the [official Data Protection configuration
documentation](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).

**Backup and restore.** Stop the application and back up the entire `data` directory
confidentially, following the SQLite consistency procedure above; include the whole
`admin-keys` ring, including expired keys, and configuration. Restore it to the same
data mount before startup, preserving/reapplying runtime ownership and private
permissions. The encrypted `admin_session` table must be backed up together with this ring.
Before starting after a historical database restore, clear `admin_session` as described below.
Losing an original key makes its protected payloads unreadable; affected sessions
require a fresh login and cannot be recovered from token plaintext.
The current external-token login does not consume these keys.

**Rollback.** Rolling back this infrastructure change leaves current login behavior
unchanged. Keep `admin-keys` in the data mount for a subsequent upgrade; rolling back
code is not a reason to remove the ring or discard its backup.

## Encrypted administrator session storage

The additive `AddAdminSessions` migration creates an empty `admin_session` table
and an expiry/hash index without changing vocabulary data. This release registers
storage, bounded cleanup and an internal session authentication/sign-in service.
This storage layer does not enable refresh or change the production password
login and frontend flow.
The table contains SHA-256 handle digests, UTC Unix millisecond deadlines and Data
Protection ciphertext. Token/identity data is bound to each row's digest and deadline.
Only trusted Host callers with independently verified tokens may create sessions.
The access token's verified `exp` determines TTL, with no sliding expiry.

Reads reject exact expiry, explicit revocation, damaged payloads and missing keys.
Store failures are separate safe errors; expiry enforcement works even if the
minute-based cleanup cannot run. Cleanup uses at most 100 rows per batch, logs only
a fixed safe diagnostic on failure and retries next minute. It does not stop an
otherwise healthy host when database initialization is disabled.

Create/replace/revoke share the vocabulary write transaction lock. Replace commits
new creation and old deletion together; two revocations return at most one valid
snapshot. A snapshot already read by a started request is not retroactively revoked.
Precommit cancellation or failure rolls back; after commit begins, losing the result
is an unknown outcome and callers must not automatically replay creation/replacement.
This remains a single-instance guarantee, without upstream/global token revocation.

**Restore procedure:** stop the application, restore SQLite consistently and the
complete key ring together, then use a SQLite tool to run `DELETE FROM admin_session;`
against the restored database if the table exists, before restarting. Require all
administrator sessions to sign in again. A historical backup can contain sessions
revoked since the backup; clearing the table prevents their resurrection. Protect
both backup and runtime files: an attacker with the database and keys, or runtime
user/root access, can decrypt the payloads.

**Rollback:** an older application ignores this additive table; retain the table
and ring when rolling code back so a later upgrade can read unexpired sessions.
Running the migration Down drops only the session table and forces fresh login;
all vocabulary rows survive. See the [database contract](../database/README.md#encrypted-administrator-session-storage).

## Administrator session authentication

Trusted Host code can use `IAdminSessionSignIn` after independently validating the
access token's signature, issuer, audience, token-to-principal association and exact
`exp`, and any applicable OIDC ID-token checks. This internal interface is not an
HTTP endpoint. It requires an authenticated administrator principal with issuer,
subject and a future `exp`; structural and role checks do not replace token validation.
Only the encrypted server-side payload contains access/ID tokens. Responses and the
new cookie contain no token. Password/OIDC password-grant/Gateway login continues
issuing the configured legacy HttpOnly JWT cookie with its existing Strict/secure
settings and response envelope; optional OIDC Code login uses the server-side session described below.

Each request selects exactly one authentication source:

1. An `Authorization` value starting with `Bearer ` (case insensitive) selects
   Bearer, including empty or invalid values.
2. Otherwise the presence of `__Host-Lexarbor.AdminSession`, including an empty
   value, selects the encrypted server-side session.
3. Otherwise the configured legacy JWT cookie uses the existing JWT validation.

Invalid, expired or unreadable selected credentials return 401 without falling back
or mixing roles from another source. Authenticated users without the configured role
receive 403. Challenges remain JSON, with no HTML redirect. `/admin/auth/session`
retains `{success:true,data:{username,roles}}`; public API and health stay anonymous.
For authenticated cookie requests, all non-safe `/admin` methods require exactly
`X-Requested-With: XMLHttpRequest`; Bearer requests are exempt even when carrying
both cookies. Unauthenticated protected writes return 401. An invalid new cookie
therefore also suppresses a valid legacy cookie. To switch from an internal session
to a password/Gateway login, log out first; legacy login does not replace the selected
new session cookie.

The new cookie name is fixed, with HttpOnly, Secure, SameSite=Lax, Path=/ and no
Domain. HTTPS is required even if `AdminAuthentication:CookieSecure` is false for the
legacy cookie. Its Expires/Max-Age never exceeds the independently verified access
`exp`; there is no sliding renewal. The handler rechecks exact expiry and protected
payload integrity on every request and reconstructs only identity/roles, never token
claims. Restart with the same database and key ring preserves unexpired sessions.

Internal sign-in atomically creates the new row and revokes the presented old new
handle, then writes the cookie and deletes the legacy cookie only after confirmed
commit. Precommit failure/cancellation leaves the previous session and browser cookie
intact. A lost result after commit has started is unknown; never automatically retry
or claim that the old row survived. A cookie arriving after its handle was revoked
cannot revive it. Concurrent replacements can create independent new rows: this is
not a global single-session guarantee for a browser. Requests already authenticated
before revocation may finish; future reads of the revoked handle fail.

`POST /admin/auth/logout` atomically revokes any presented new handle (also when
Bearer is selected), deletes both cookies and returns the existing 200 envelope.
A copied handle then fails authentication. Missing/invalid/expired/damaged handles
and repeated logout safely return 200; a cookie-authenticated logout missing the
CSRF header returns 403 without deleting cookies or revoking the row. Bearer tokens
remain valid: browser logout never revokes already issued upstream tokens. In
`OidcCode` mode the envelope can additionally carry an optional `data.logoutUrl`
when a prepared upstream SignaCore logout succeeded; see
[prepared upstream logout](#prepared-upstream-logout).

If session reading during authentication or revocation during logout cannot complete,
logout clears both browser cookies and returns a fixed safe 500, or 503 with
`Retry-After: 1` for busy storage. This does **not** confirm that a copied handle was
revoked; it may remain usable after storage recovers. Losing the commit result is
also unknown. No handle, token, ciphertext, SQL or provider exception is logged.

**Authentication rollback:** stop new internal sign-ins, clear the new cookie in the
browser using its exact Path=/ and Secure attributes, and require legacy login again.
Keep the additive table and key ring; do not run migration Down or convert handles to
JWT cookies. The two cookie formats are not interchangeable. A historical database
restore still requires clearing `admin_session` before startup as described above.

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

[SignaCore](https://github.com/philfanzhou/SignaCore) works with the default `Oidc` provider through its RFC 6749 token endpoint, `/oauth2/token`. SignaCore also provides hosted authorization-code login, available with the optional `OidcCode` mode below. Administrators currently sign in through the Lexarbor login form and Lexarbor performs the password grant server-side; the browser never receives the client secret or the access token.

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

## Pending hosted-login transactions

`PendingAdminLoginStore` is the singleton used by hosted login for one Host instance.
It holds at most 4096 active transactions for five minutes. At capacity it reclaims
expired entries and rejects creation without evicting active entries. Restarting
the Host loses pending logins, so users must start login again; existing persisted
administrator sessions are unaffected. Pending state is not stored in SQLite or
the Data Protection key ring and cannot be recovered across instances/restarts.

Each transaction uses fresh 32-byte random state, nonce, PKCE verifier and browser
binding (canonical 43-character unpadded base64url), with an S256 challenge. The
store indexes only the SHA-256 state hash and stores a browser-binding hash, expiry,
nonce/verifier and validated return target. Creation exposes no verifier; only a
successful internal consume returns it. There are no code/token/secret/identity
fields, and correlation material must never be logged.

HTTP routes use a separate `__Host-Lexarbor.Login.<state>` cookie for
each transaction, with HttpOnly, Secure, SameSite=Lax, Path=/, no Domain and a
five-minute lifetime. Parallel starts cannot overwrite a shared binding cookie.
Callbacks validate unique fields and exact issuer before consuming state/browser
binding; only one concurrent consumer succeeds, and the exact deadline is expired.
A wrong browser cannot consume the valid transaction. Clear only that transaction's
cookie with its original attributes. Cancellation/denial also consumes; failures
after consumption never revive the transaction. Cancellation before admission to
mutation leaves the valid pending transaction intact. Creating or rejecting a
pending transaction never changes an existing administrator session.

Return targets default to `/#/books` when omitted and accept only `/books`,
`/books/<id>/words`, `/vocabulary`, `/phrases`, `/import`, `/import/phrase` and
`/import/batch`, optionally with the `/#` prefix. IDs are 1–128 ASCII letters,
digits, `-` or `_`; inputs are at most 256 characters. The store saves the normalized
`/#/...` target. Queries, extra fragments, percent encoding (including double or
invalid encoding), absolute URLs, double slashes, backslashes, controls, login,
forbidden and unknown routes fail closed. Navigation uses this stored target,
never a new callback-supplied target. Rollback discards only in-memory pending state;
no database, migration or persistence-directory change is needed.

## Authorization-code validation

The Host uses `AdminCodeExchange` for Confidential SignaCore hosted login.
The default password/Gateway/Bearer behavior remains available.
Incomplete unused Code settings do not prevent the existing service from starting.

Its settings are `AdminAuthentication:OidcCode:ClientId`, `ClientSecret`, `RedirectUri`
and `Scope` (default `openid profile`), alongside the existing `IdentityService`
trust settings. The resource audience must exactly equal the client ID
(PerApplication). Scope must contain `openid` and may contain only `profile` in
addition; `offline_access` and refresh are not enabled. Keep the secret in a
protected server configuration source. Container aliases are listed below.

Before activation, register a Confidential SignaCore application, change its
audience to PerApplication, register the exact callback, then enable Code with
`openid`/`profile` and refresh disabled. The callback must be an ASCII HTTPS URI of
at most 500 characters with path `/admin/auth/callback`, no userinfo or fragment.
Only Development/Testing may use HTTP callbacks on numeric `127.0.0.1` or `[::1]`;
`localhost` is not an HTTP callback. Use the exact registered URI, including any
query, without rewriting it. Behind a proxy, register the external HTTPS URL and
configure the existing trusted-forwarding boundary.

The service shares the Bearer Discovery/JWKS cache but checks exact issuer and
same scheme/host/port authorization, token and JWKS endpoints independently.
Production endpoints require HTTPS; authorization/token endpoints cannot have
existing query parameters. Exchange is one `client_secret_post` form POST with
PKCE and no scope or Basic credentials, without redirect following or automatic
retry. The backchannel disables HTTP client loggers and cookies, applies a
30-second deadline including response reading, and accepts at most 64 KiB.
Never retry an authorization code: SignaCore can revoke the upstream session on
replay. Only already received tokens may be revalidated after a key refresh.

Both tokens must independently pass RS256/JWKS `kid`, exact issuer, single audience,
separate `JWT`/`at+jwt` type, issue/expiry time, and matching single subject checks;
the ID token must match the pending nonce. Expired tokens fail at exact `exp`.
SMS and password `amr` are both accepted. Only the validated access token can grant
the configured administrator role; ID profile/roles cannot authorize. The internal
result carries tokens only for trusted Host callers and never creates a session.
Failures expose fixed classifications without upstream bodies or raw exceptions;
caller cancellation propagates. The callback first validates and consumes
its browser-bound transaction and must keep all tokens/verifiers/secrets server-side.
No database, migration, persistence directory or public JSON contract changes here.

## Hosted authorization-code login (optional)

Register a Confidential SignaCore application first, select PerApplication audience,
register the exact external HTTPS `/admin/auth/callback` URI, enable authorization
code with PKCE S256 and `openid profile`, and supply the administrator role through
the access token. Only then select `AdminAuthentication:Provider=OidcCode`. The
client ID and `IdentityService:Audience` must both be the application ID; issuer
must exactly match Discovery. Set `IdentityService:Authority` to the trusted HTTPS
provider. SignaCore Code access tokens last 15 minutes; the session ends at exact
access-token `exp`, with no refresh. Code scope defaults to `openid profile`;
SignaCore's older password grant instead requires the **empty** scope documented
above. Do not copy password-grant settings into the Code section.

| Container script variable | Configuration key |
|---|---|
| `LEXARBOR_OIDC_CODE_CLIENT_ID` | `AdminAuthentication:OidcCode:ClientId` |
| `LEXARBOR_OIDC_CODE_CLIENT_SECRET` | `AdminAuthentication:OidcCode:ClientSecret` |
| `LEXARBOR_OIDC_CODE_REDIRECT_URI` | `AdminAuthentication:OidcCode:RedirectUri` |
| `LEXARBOR_OIDC_CODE_POST_LOGOUT_REDIRECT_URI` | `AdminAuthentication:OidcCode:PostLogoutRedirectUri` |
| `LEXARBOR_OIDC_CODE_SCOPE` | `AdminAuthentication:OidcCode:Scope` |

These independent overrides never rewrite a pre-existing `/app/data/appsettings.json`.
Use `LEXARBOR_ADMIN_AUTH_PROVIDER=OidcCode` and the existing identity variables.
Protect the secret as server configuration; never put it in frontend settings.
A registered static callback query is kept byte-for-byte, but duplicate fields or
reserved `state`, `iss`, `code`, `error`, `error_description` fields are refused.
Behind a proxy use the registered external HTTPS URI and trusted client-address
forwarding; the callback is never inferred from untrusted request headers.

`GET /admin/auth/method` returns only `{success:true,data:{method:"hosted"}}` in
Code mode (`password` otherwise). Navigate to `GET /admin/auth/start`, optionally
with one `returnUrl` from the route allowlist above. It shares the password login's
per-IP quota and 429/Retry-After contract. Invalid return targets give 400; missing
Code configuration or full pending capacity gives 503, and failed/untrusted
Discovery gives 502, without creating a login cookie or changing existing sessions.
Old modes safely refuse hosted routes. Code mode rejects every
`POST /admin/auth/login` with 400 before parsing a password body; existing cookie
CSRF protection still applies. Configuration is selected at startup.

Start uses Discovery's authorization endpoint with unique supported fields, no
`response_mode`. Callback requires unique state/issuer and exactly one code or
allowlisted error, validates browser binding, then atomically consumes the transaction
and clears only its cookie. Success validates both tokens and the access role,
commits the encrypted session, and redirects to the stored local hash target.
Failures immediately redirect to `/#/login?reason=canceled|denied|sign_in_failed|provider_unavailable`
with a fixed classification, never upstream error descriptions. Method/start/callback
send `Cache-Control: no-store`, and callback sends `Referrer-Policy: no-referrer`.
Precommit failure or cancellation preserves the existing new/legacy session;
after commit begins an interrupted/lost result is unknown. Never replay the callback:
read `/admin/auth/session` to establish the current state or begin a new login.
Different transactions remain independent; no global single-session guarantee.

The incoming callback necessarily contains a one-time authorization code, and the
prepared-logout return route receives a one-time logout state in its query. Neither
may be copied into response bodies, application logs or analytics. Host filters
suppress framework request URL and HTTP body logging even at Trace; the Code HTTP
clients have no loggers and no redirect following. Configure **every reverse proxy**
to omit the callback and logout-return query strings, and do not enable request/body
analytics for these routes. Routing matches these routes case-insensitively and with
one optional trailing slash, so the masking must cover every accepted form. For
example, in nginx's `http` context (`~*` is a case-insensitive regular expression):

```nginx
map $uri $lexarbor_log_target {
    default $request_uri;
    ~*^/admin/auth/callback/?$ $uri;
    ~*^/admin/auth/logout/return/?$ $uri;
}
log_format lexarbor_safe '$remote_addr $request_method $lexarbor_log_target $status';
access_log /var/log/nginx/lexarbor.access.log lexarbor_safe;
```

The frontend password form is not switched in this backend release (#155); Code
users navigate directly to `/admin/auth/start`, and the frontend consumption of the
prepared-logout `data.logoutUrl` and its `reason` values belongs to the same UI
rollout. To roll back, stop new Code logins, restore `Oidc`/`Gateway` configuration,
clear the opaque cookie and sign in through the password flow. Keep the database and
key ring; never convert handles to JWTs. Restart discards pending login and
logout-return transactions and requires a fresh start.

### Prepared upstream logout

In Code mode, `POST /admin/auth/logout` can also end the browser's SignaCore
session through SignaCore's prepared logout. The local session always ends first:
the endpoint atomically revokes the presented handle and clears both cookies, and
only when the revoked session really held an ID token does the server then send the
preparation request — `POST /oauth2/logout/requests` on the same trusted issuer
(Discovery deliberately publishes no end-session endpoint) — with the same
confidential client form authentication as the token endpoint. The ID token and the
client secret stay server-side and never reach the browser, logs or URLs. The
dedicated backchannel has no loggers, no redirect following, no cookies and a
30-second deadline, accepts at most 4 KiB, and sends at most one POST per revoked
session, never retried. Callers without a live session snapshot — no session, a
legacy JWT cookie, an expired, damaged or repeated logout — never trigger an
upstream call and never fabricate a hint; at most one concurrent logout obtains the
snapshot.

On success the 200 envelope adds an optional `data.logoutUrl`: the verified,
one-time SignaCore logout URI the browser may be navigated to. Only a URI whose
scheme, host and port exactly match the trusted issuer, whose path is
`/oauth2/logout`, and whose sole query field is a canonical 43-character base64url
`logout_handle` is ever exposed; the handle is the only upstream value a browser
sees. Any other outcome — upstream unreachable, timeout, non-2xx, malformed or
oversized body, untrusted URI shape — leaves the plain `{"success":true}` envelope.
In hosted mode a missing `logoutUrl` means local-only logout: the UI must say that
the identity provider may still hold a session and must never claim SignaCore
signed out. No upstream error text is echoed. Code-mode logout responses carry
`Cache-Control: no-store` because they can contain the one-time URI; old
password/Gateway modes and Bearer semantics keep their previous responses, and the
cookie CSRF rule still answers 403 before anything is revoked.

SignaCore completes the browser navigation to `logoutUrl` with a handle that is
valid for five minutes and works exactly once; a missing, malformed, expired or
consumed handle is answered locally by SignaCore with a fixed 400 and no redirect.
The completion answer looks the same whether or not the browser still had an
upstream session, so nothing may be inferred from it.

To receive the browser back, set
`AdminAuthentication:OidcCode:PostLogoutRedirectUri`
(`LEXARBOR_OIDC_CODE_POST_LOGOUT_REDIRECT_URI`) to this deployment's exact
`https://<external-host>/admin/auth/logout/return` URI and register the identical
URI as the application's post-logout URI in SignaCore; the provider matches it
byte-for-byte with no normalization. Only an ASCII HTTPS URI of at most 500
characters is accepted (HTTP only on numeric `127.0.0.1`/`[::1]` in
Development/Testing), its path must be `/admin/auth/logout/return`, and an optional
registered static query must not repeat fields or contain `state`, which SignaCore
appends. When the setting is absent or invalid, preparation proceeds without the
return pair: SignaCore shows its own signed-out page and the browser does not come
back to Lexarbor.

`GET /admin/auth/logout/return` is the fixed anonymous return route. It accepts
exactly one canonical `state`, matches it against a one-time browser-binding cookie
(`__Host-Lexarbor.Logout.<state>`, HttpOnly/Secure/SameSite=Lax, Path=/, no Domain,
five minutes) issued with the successful logout response, consumes the pair exactly
once, deletes only its own cookie and answers with fixed in-site redirects:
`/#/login?reason=logged_out` on success and `/#/login?reason=logout_failed` for a
missing, malformed, unknown, expired, duplicate or unbound state — one shape for
every failure, never echoing input, never targeting an external site, never
establishing a session. Both answers carry `Cache-Control: no-store` and
`Referrer-Policy: no-referrer`. States live only in this Host instance's memory
(hashes only, no database or persistence change), so a restart discards them and
the return then reports `logout_failed` — the same accepted single-instance
boundary as pending login transactions. The `reason` values extend the existing
bounded enum; the current frontend ignores unknown reasons harmlessly until the UI
rollout consumes them.

Guarantees and limits: a normal flow ends both the Lexarbor session and the current
browser's SignaCore session; every failure path still ends the Lexarbor session.
Already issued access tokens are **not** revoked by logout — they stay valid
downstream until their exact `exp` (15 minutes in Code mode). Other applications'
local sessions are untouched, refresh-token revocation is not part of this flow,
and when preparation fails the upstream session may survive until SignaCore's own
idle/absolute limits end it. A lost local revocation commit remains unknown and is
never retried or compensated.

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
| `Network:ForwardLimit` | `1` | Trusted hops in front of Lexarbor; must be 1–10 |

Nothing is trusted until one of these is set, and forwarded headers are ignored entirely
until then. That default is deliberate: `X-Forwarded-For` is client-supplied, and honouring
it from an unknown source would let any caller mint a fresh partition key per request and
pass the ceiling without ever reaching it. A degraded shared limit is a visible operational
problem; a bypassable limit is an invisible security one. Set `ForwardLimit` to the real
number of trusted hops — raising it further hands the extra steps back to the client, whose
own header content occupies the left of the list.

Trusted forwarding is provided by `ServiceMantle.Web` v0.2.0. Configure the proxy to
send matching `X-Forwarded-For` and `X-Forwarded-Proto` lists, one entry per hop;
asymmetric headers are ignored. Invalid addresses, CIDR ranges, or hop limits fail
startup. Forwarding remains disabled with the default empty trust lists. This
integration enables only forwarded headers: it does not add ServiceMantle setup,
management, phase, health, or rate-limit endpoints. The existing Lexarbor login
and public API ceilings and their 429 responses remain in effect.

Startup logs both ceilings and whether any hop is trusted, so a misconfigured proxy is
visible in the first lines of the container log.

## Health and smoke checks

```bash
curl http://localhost:5008/health
curl -i http://localhost:5008/admin/vocabulary-books
curl http://localhost:5008/api/vocabulary-books/all
```

Expected results: health returns 200; an anonymous administration request returns 401; the public book request returns a success envelope whose `data.books` is empty on a new instance.

The administrator shared-word replacement API requires no configuration or database migration. Changes affect the shared word in every book, including disabled books. The conflict check normalizes historical spelling with .NET Unicode casing and whitespace rules at request time; it does not rewrite stored rows or indexes. It may scan the catalogue while holding the existing write transaction, so larger catalogues can take longer to edit. The administration word detail drawer is the dependent UI: its save confirms every referencing book, disabled ones included, before sending the full three-field replacement, and rolling back the frontend removes that entry point. Take a consistent SQLite backup before maintenance: rolling back application code removes the new API but does not restore previous spelling or phonetics. Restore prior data from the backup when needed, and roll back any dependent UI before the API. Disconnecting after submitting a write does not undo a commit; query state after an unknown result instead of blindly replaying it.

The book-owned meaning replacement API needs no configuration or schema migration. The same detail drawer edits one meaning at a time, scoped to its book, word, and meaning identifiers; a save that never answers re-reads the current detail instead of replaying. Back up SQLite consistently before maintenance. Rolling back application code removes the endpoint but does not restore edited definitions or examples; use a pre-operation backup for data recovery, and roll back dependent UI before the API. If a write response is lost, query current state before deciding on another write; disconnecting cannot promise that an admitted transaction was reversed.

Administrator vocabulary GET routes include disabled-book and unassigned vocabulary for maintenance. These are read-only deferred SQLite snapshots; no configuration, schema migration, startup cleanup, or persistence-directory change is required. Rolling back the API removes these new reads without changing stored data. Back up the SQLite database consistently before any separate editing or cleanup operation; read requests themselves do not alter it.

## Vocabulary cleanup and recovery

Only authorized administrators can preview and commit book-scoped cleanup. Preview is read-only; commit may permanently remove meanings, words which lose their last reference, and optionally the book. Disabled books still count as references. A synthetic or real book left by an older release can be removed explicitly through this API; startup never cleans it automatically.

The administration frontend is the dependent UI for this API: 教材管理 offers 清空内容 and 删除教材及内容, the book word list removes the current page's checked words or one row, and the word detail drawer deletes a single meaning. Every entry previews first, shows the estimated counts, and requires the exact book name for a whole-book delete; the plain book delete keeps refusing a referenced book, so it never becomes a silent cascade. The frontend re-queries current state after an unknown commit outcome and never replays one; rolling back the frontend removes these entry points while the API remains.

Take a consistent SQLite backup using the backup procedure above before destructive maintenance. Code rollback removes the API but does not restore deleted records: restore the pre-operation backup for recovery. Roll back dependent UI before the API. No configuration, schema migration, new persistent table, or volume change is required.

Recheck the current scope and exact book name before delete. Preview is only an estimate and concurrent imports can change final counts. If the connection drops or the response is lost, query current state before deciding on another operation. In particular, repeating clear can erase newly imported data. There is no automatic retry, idempotency key or undo; disconnecting after admission does not cancel an already committed write.
