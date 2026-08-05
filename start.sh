#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_TAG="20260724"
IMAGE_NAME="ruoyu.study.vocabulary:${IMAGE_TAG}"
CONTAINER_NAME="ruoyu-vocabulary"
NETWORK_NAME="ruoyu-net"
# HTTP listen port is hardcoded to 5008 inside the container (Program.cs).
# Port is the host port mapped to the container's 5008.
Port="5008"

CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR:-host.docker.internal:8500}"
CONSUL_TOKEN="${CONSUL_TOKEN:-}"
ADMIN_AUTH_PROVIDER="${VOCABULARY_ADMIN_AUTH_PROVIDER:-QuantumZhou}"
IDENTITY_APP_ID="${VOCABULARY_IDENTITY_APP_ID:-}"
IDENTITY_APP_SECRET="${VOCABULARY_IDENTITY_APP_SECRET:-}"
COOKIE_SECURE="${VOCABULARY_COOKIE_SECURE:-false}"

DB_NAME="ruoyu_study_vocabulary"

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
  -e TZ=Asia/Shanghai \
  -e CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR}" \
  -e CONSUL_TOKEN="${CONSUL_TOKEN}" \
  -e Database__Name="${DB_NAME}" \
  -e AdminAuthentication__Provider="${ADMIN_AUTH_PROVIDER}" \
  -e AdminAuthentication__QuantumZhou__AppId="${IDENTITY_APP_ID}" \
  -e AdminAuthentication__QuantumZhou__AppSecret="${IDENTITY_APP_SECRET}" \
  -e AdminAuthentication__CookieSecure="${COOKIE_SECURE}" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
docker logs -f -t "$CONTAINER_NAME"
