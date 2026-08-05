#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
IMAGE_NAME="${LEXARBOR_IMAGE:-lexarbor:latest}"
CONTAINER_NAME="${LEXARBOR_CONTAINER_NAME:-lexarbor}"
NETWORK_NAME="${LEXARBOR_NETWORK:-lexarbor-net}"
HOST_PORT="${LEXARBOR_PORT:-5008}"
DATA_DIR="${LEXARBOR_DATA_DIR:-${SCRIPT_DIR}/data}"

ADMIN_AUTH_PROVIDER="${LEXARBOR_ADMIN_AUTH_PROVIDER:-Oidc}"
IDENTITY_AUTHORITY="${LEXARBOR_IDENTITY_AUTHORITY:-http://host.docker.internal:8080}"
IDENTITY_ISSUER="${LEXARBOR_IDENTITY_ISSUER:-${IDENTITY_AUTHORITY}}"
IDENTITY_AUDIENCE="${LEXARBOR_IDENTITY_AUDIENCE:-lexarbor}"
COOKIE_SECURE="${LEXARBOR_COOKIE_SECURE:-false}"

OIDC_TOKEN_ENDPOINT="${LEXARBOR_OIDC_TOKEN_ENDPOINT:-}"
OIDC_CLIENT_ID="${LEXARBOR_OIDC_CLIENT_ID:-}"
OIDC_CLIENT_SECRET="${LEXARBOR_OIDC_CLIENT_SECRET:-}"
OIDC_SCOPE="${LEXARBOR_OIDC_SCOPE:-openid profile}"

GATEWAY_AUTHORITY="${LEXARBOR_GATEWAY_AUTHORITY:-}"
GATEWAY_TOKEN_PATH="${LEXARBOR_GATEWAY_TOKEN_PATH:-/api/auth/token}"
GATEWAY_APP_ID="${LEXARBOR_GATEWAY_APP_ID:-}"
GATEWAY_APP_SECRET="${LEXARBOR_GATEWAY_APP_SECRET:-}"

mkdir -p "$DATA_DIR"
docker network inspect "$NETWORK_NAME" >/dev/null 2>&1 || docker network create "$NETWORK_NAME"

if docker container inspect "$CONTAINER_NAME" >/dev/null 2>&1; then
    docker rm -f "$CONTAINER_NAME" >/dev/null
fi

docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  --network "$NETWORK_NAME" \
  --add-host=host.docker.internal:host-gateway \
  -p "${HOST_PORT}:5008" \
  -v "${DATA_DIR}:/app/data" \
  -e TZ="${TZ:-UTC}" \
  -e ConnectionStrings__Default="Data Source=/app/data/vocabulary.db" \
  -e IdentityService__Authority="${IDENTITY_AUTHORITY}" \
  -e IdentityService__Issuer="${IDENTITY_ISSUER}" \
  -e IdentityService__Audience="${IDENTITY_AUDIENCE}" \
  -e AdminAuthentication__Provider="${ADMIN_AUTH_PROVIDER}" \
  -e AdminAuthentication__CookieSecure="${COOKIE_SECURE}" \
  -e AdminAuthentication__Oidc__TokenEndpoint="${OIDC_TOKEN_ENDPOINT}" \
  -e AdminAuthentication__Oidc__ClientId="${OIDC_CLIENT_ID}" \
  -e AdminAuthentication__Oidc__ClientSecret="${OIDC_CLIENT_SECRET}" \
  -e AdminAuthentication__Oidc__Scope="${OIDC_SCOPE}" \
  -e AdminAuthentication__Gateway__Authority="${GATEWAY_AUTHORITY}" \
  -e AdminAuthentication__Gateway__TokenPath="${GATEWAY_TOKEN_PATH}" \
  -e AdminAuthentication__Gateway__AppId="${GATEWAY_APP_ID}" \
  -e AdminAuthentication__Gateway__AppSecret="${GATEWAY_APP_SECRET}" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started on http://localhost:${HOST_PORT}"
docker logs --tail 20 "$CONTAINER_NAME"
