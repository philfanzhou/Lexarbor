# Deployment and operations

## Build the image

The root of the repository is the Docker build context:

```bash
docker build -f src/Host/Dockerfile -t lexarbor:latest .
```

The multi-stage build compiles the Vue frontend, publishes the .NET backend, and copies the static files into the Host output. The runtime image exposes HTTP port 5008 and stores all durable state in `/app/data/vocabulary.db`.

## Start the container

```bash
bash start.sh
```

`start.sh` creates a `lexarbor-net` Docker network, replaces an existing container with the same name, mounts the data directory, and starts the image. Its general settings are:

| Environment variable | Default | Purpose |
|---|---|---|
| `LEXARBOR_IMAGE` | `lexarbor:latest` | Image to run |
| `LEXARBOR_CONTAINER_NAME` | `lexarbor` | Container name |
| `LEXARBOR_NETWORK` | `lexarbor-net` | Docker network |
| `LEXARBOR_PORT` | `5008` | Host port mapped to container port 5008 |
| `LEXARBOR_DATA_DIR` | `<repository>/data` | Host directory mounted at `/app/data` |

The deployment is single-instance only. Do not mount the same SQLite file into multiple running containers.

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
| `LEXARBOR_IDENTITY_AUTHORITY` | `http://host.docker.internal:8080` | `IdentityService:Authority` |
| `LEXARBOR_IDENTITY_ISSUER` | same as Authority | `IdentityService:Issuer` |
| `LEXARBOR_IDENTITY_AUDIENCE` | `lexarbor` | `IdentityService:Audience` |
| `LEXARBOR_ADMIN_AUTH_PROVIDER` | `Oidc` | `AdminAuthentication:Provider` |
| `LEXARBOR_OIDC_TOKEN_ENDPOINT` | discovery document | `AdminAuthentication:Oidc:TokenEndpoint` |
| `LEXARBOR_OIDC_CLIENT_ID` | empty | `AdminAuthentication:Oidc:ClientId` |
| `LEXARBOR_OIDC_CLIENT_SECRET` | empty | `AdminAuthentication:Oidc:ClientSecret` |
| `LEXARBOR_OIDC_SCOPE` | `openid profile` | `AdminAuthentication:Oidc:Scope` |
| `LEXARBOR_COOKIE_SECURE` | `false` | `AdminAuthentication:CookieSecure` |

The validated token must contain `role=admin` by default. Override `AdminAuthentication__RequiredRole` to use another role. Set `LEXARBOR_COOKIE_SECURE=true` whenever the browser accesses Lexarbor over HTTPS. Missing credential-provider settings do not prevent startup; administration login returns 503 until configured.

Example:

```bash
export LEXARBOR_IDENTITY_AUTHORITY=https://identity.example.com
export LEXARBOR_IDENTITY_ISSUER=https://identity.example.com
export LEXARBOR_IDENTITY_AUDIENCE=lexarbor
export LEXARBOR_OIDC_CLIENT_ID=lexarbor-admin
export LEXARBOR_OIDC_CLIENT_SECRET=replace-me
export LEXARBOR_COOKIE_SECURE=true
bash start.sh
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
