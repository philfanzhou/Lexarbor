#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_TAG="20260501"
IMAGE_NAME="ruoyu.study.vocabulary:${IMAGE_TAG}"
CONTAINER_NAME="ruoyu-vocabulary"
NETWORK_NAME="ruoyu-net"

#GRPC_PORT="10895"

DB_HOST="ruoyu-postgres"
DB_PORT="5432"
DB_NAME="ruoyu_study_vocabulary"
DB_USER="postgres"
DB_PASS="postgres"

CONNECTION_STRING="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASS};"

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
  -e TZ=Asia/Shanghai \
  -e ASPNETCORE_URLS="http://+:5008" \
  -e ConnectionStrings__Default="${CONNECTION_STRING}" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
echo "-> DB: ${DB_HOST}:${DB_PORT}/${DB_NAME}"
echo "-> Network: ${NETWORK_NAME}"
echo "-> Image: ${IMAGE_NAME}"
echo "=== Real-time Logs ==="
docker logs -f -t "$CONTAINER_NAME"
