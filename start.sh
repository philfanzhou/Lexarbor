#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_TAG="20260805"
IMAGE_NAME="ruoyu.study.vocabulary:${IMAGE_TAG}"
CONTAINER_NAME="ruoyu-vocabulary"
NETWORK_NAME="ruoyu-net"
# HTTP listen port is hardcoded to 5008 inside the container (Program.cs).
# Port is the host port mapped to the container's 5008.
Port="5008"

ADMIN_AUTH_PROVIDER="${VOCABULARY_ADMIN_AUTH_PROVIDER:-QuantumZhou}"
IDENTITY_APP_ID="${VOCABULARY_IDENTITY_APP_ID:-}"
IDENTITY_APP_SECRET="${VOCABULARY_IDENTITY_APP_SECRET:-}"
IDENTITY_AUTHORITY="${VOCABULARY_IDENTITY_AUTHORITY:-http://ruoyu-identity:5002}"
COOKIE_SECURE="${VOCABULARY_COOKIE_SECURE:-false}"
DATA_DIR="${VOCABULARY_DATA_DIR:-${SCRIPT_DIR}/data}"

mkdir -p "$DATA_DIR"

docker network inspect "$NETWORK_NAME" >/dev/null 2>&1 || docker network create "$NETWORK_NAME"

if [ -n "$(docker ps -q --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Container is already running, stopping it..."
    docker stop "$CONTAINER_NAME"
fi
if [ -n "$(docker ps -aq --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Removing old container..."
    docker rm "$CONTAINER_NAME"
fi

docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  --network "$NETWORK_NAME" \
  --add-host=host.docker.internal:host-gateway \
  -p "${Port}:5008" \
  -v "${DATA_DIR}:/app/data" \
  -e TZ=Asia/Shanghai \
  -e ConnectionStrings__Default="Data Source=/app/data/vocabulary.db" \
  -e IdentityService__Authority="${IDENTITY_AUTHORITY}" \
  -e AdminAuthentication__Provider="${ADMIN_AUTH_PROVIDER}" \
  -e AdminAuthentication__QuantumZhou__AppId="${IDENTITY_APP_ID}" \
  -e AdminAuthentication__QuantumZhou__AppSecret="${IDENTITY_APP_SECRET}" \
  -e AdminAuthentication__CookieSecure="${COOKIE_SECURE}" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
docker logs -f -t "$CONTAINER_NAME"
