#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
IMAGE_NAME="${LEXARBOR_IMAGE:-lexarbor:latest}"
CONTAINER_NAME="${LEXARBOR_CONTAINER_NAME:-lexarbor}"
NETWORK_NAME="${LEXARBOR_NETWORK:-lexarbor-net}"
HOST_PORT="${LEXARBOR_PORT:-5008}"
DATA_DIR="${LEXARBOR_DATA_DIR:-${REPOSITORY_ROOT}/data}"

APP_ENVIRONMENT=()

add_configuration_override() {
  local source_name="$1"
  local target_name="$2"
  local source_value
  if source_value="$(printenv "$source_name")"; then
    APP_ENVIRONMENT+=("-e" "${target_name}=${source_value}")
  fi
}

add_configuration_override LEXARBOR_IDENTITY_AUTHORITY IdentityService__Authority
if identity_issuer="$(printenv LEXARBOR_IDENTITY_ISSUER)"; then
  APP_ENVIRONMENT+=("-e" "IdentityService__Issuer=${identity_issuer}")
elif identity_authority="$(printenv LEXARBOR_IDENTITY_AUTHORITY)"; then
  APP_ENVIRONMENT+=("-e" "IdentityService__Issuer=${identity_authority}")
fi
add_configuration_override LEXARBOR_IDENTITY_AUDIENCE IdentityService__Audience
add_configuration_override LEXARBOR_REQUIRE_HTTPS_METADATA IdentityService__RequireHttpsMetadata
add_configuration_override LEXARBOR_ADMIN_AUTH_PROVIDER AdminAuthentication__Provider
add_configuration_override LEXARBOR_COOKIE_SECURE AdminAuthentication__CookieSecure
add_configuration_override LEXARBOR_OIDC_TOKEN_ENDPOINT AdminAuthentication__Oidc__TokenEndpoint
add_configuration_override LEXARBOR_OIDC_CLIENT_ID AdminAuthentication__Oidc__ClientId
add_configuration_override LEXARBOR_OIDC_CLIENT_SECRET AdminAuthentication__Oidc__ClientSecret
add_configuration_override LEXARBOR_OIDC_SCOPE AdminAuthentication__Oidc__Scope
add_configuration_override LEXARBOR_GATEWAY_AUTHORITY AdminAuthentication__Gateway__Authority
add_configuration_override LEXARBOR_GATEWAY_TOKEN_PATH AdminAuthentication__Gateway__TokenPath
add_configuration_override LEXARBOR_GATEWAY_APP_ID AdminAuthentication__Gateway__AppId
add_configuration_override LEXARBOR_GATEWAY_APP_SECRET AdminAuthentication__Gateway__AppSecret

mkdir -p "$DATA_DIR"
docker network inspect "$NETWORK_NAME" >/dev/null 2>&1 || docker network create "$NETWORK_NAME"

if docker container inspect "$CONTAINER_NAME" >/dev/null 2>&1; then
    docker rm -f "$CONTAINER_NAME" >/dev/null
fi

# The image runs as its own unprivileged user, which is right for a named or
# anonymous volume. A host bind mount keeps the host's ownership instead, so the
# container is run as the user who owns DATA_DIR. That also leaves the database
# and configuration files owned by whoever runs this script rather than by root,
# which is what made backing them up require sudo before.
docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  --network "$NETWORK_NAME" \
  --add-host=host.docker.internal:host-gateway \
  --user "$(id -u):$(id -g)" \
  -p "${HOST_PORT}:5008" \
  -v "${DATA_DIR}:/app/data" \
  -e TZ="${TZ:-UTC}" \
  "${APP_ENVIRONMENT[@]}" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started on http://localhost:${HOST_PORT}"
docker logs --tail 20 "$CONTAINER_NAME"
