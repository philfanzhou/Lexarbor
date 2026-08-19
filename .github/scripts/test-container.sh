#!/usr/bin/env bash
set -Eeuo pipefail

IMAGE_NAME="${1:-lexarbor:ci}"
# Optional. When given, the image must report exactly this version, which is how
# the release workflow's version argument is proven to reach the application.
EXPECTED_VERSION="${2:-}"
RUN_SUFFIX="${GITHUB_RUN_ID:-local}-${RANDOM}"
UNMOUNTED_CONTAINER="lexarbor-unmounted-${RUN_SUFFIX}"
FRESH_CONTAINER="lexarbor-fresh-${RUN_SUFFIX}"
EXISTING_CONTAINER="lexarbor-existing-${RUN_SUFFIX}"
TEST_ROOT="$(mktemp -d)"

cleanup() {
  docker rm -f -v \
    "$UNMOUNTED_CONTAINER" \
    "$FRESH_CONTAINER" \
    "$EXISTING_CONTAINER" >/dev/null 2>&1 || true
  case "$TEST_ROOT" in
    /tmp/*) rm -rf -- "$TEST_ROOT" ;;
    *) echo "Refusing to remove unexpected temporary path: $TEST_ROOT" >&2 ;;
  esac
}
trap cleanup EXIT

wait_for_health() {
  local container_name="$1"
  local mapped_port
  mapped_port="$(docker port "$container_name" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"

  for _ in $(seq 1 60); do
    if curl --fail --silent --show-error \
      "http://127.0.0.1:${mapped_port}/health" >/dev/null 2>&1; then
      return 0
    fi

    if [[ "$(docker inspect --format '{{.State.Running}}' "$container_name")" != "true" ]]; then
      break
    fi
    sleep 1
  done

  docker logs "$container_name" || true
  echo "Container ${container_name} did not become healthy" >&2
  return 1
}

check_reported_version() {
  local container_name="$1"
  local mapped_port response reported
  mapped_port="$(docker port "$container_name" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
  response="$(curl --fail --silent --show-error "http://127.0.0.1:${mapped_port}/health")"
  reported="$(printf '%s' "$response" | grep -o '"version":"[^"]*"' | cut -d '"' -f 4)"

  if [[ -z "$reported" ]]; then
    echo "Health response did not report a version" >&2
    return 1
  fi

  if [[ -n "$EXPECTED_VERSION" && "$reported" != "$EXPECTED_VERSION" ]]; then
    echo "Expected version ${EXPECTED_VERSION} but the image reports ${reported}" >&2
    return 1
  fi

  echo "Image reports version ${reported}"
}

start_container() {
  local container_name="$1"
  shift
  docker run --detach \
    --name "$container_name" \
    --publish 127.0.0.1::5008 \
    "$@" \
    "$IMAGE_NAME" >/dev/null
  wait_for_health "$container_name"
}

echo "Checking startup without an explicit host mount"
start_container "$UNMOUNTED_CONTAINER"
check_reported_version "$UNMOUNTED_CONTAINER"
docker rm -f -v "$UNMOUNTED_CONTAINER" >/dev/null

fresh_data="$TEST_ROOT/fresh"
mkdir -p "$fresh_data"

echo "Checking first-start configuration and database creation"
start_container "$FRESH_CONTAINER" --volume "$fresh_data:/app/data"
test -s "$fresh_data/appsettings.json"
test -s "$fresh_data/vocabulary.db"
cmp --silent src/Lexarbor.Host/appsettings.json "$fresh_data/appsettings.json"
docker rm -f "$FRESH_CONTAINER" >/dev/null

existing_data="$TEST_ROOT/existing"
mkdir -p "$existing_data"
cat > "$existing_data/appsettings.json" <<'JSON'
{
  "Database": {
    "InitializeOnStartup": false
  },
  "PersistenceProbe": "preserve-existing-configuration"
}
JSON
printf '%s' 'preserve-existing-database' > "$existing_data/vocabulary.db"

config_hash_before="$(sha256sum "$existing_data/appsettings.json" | cut -d ' ' -f 1)"
database_hash_before="$(sha256sum "$existing_data/vocabulary.db" | cut -d ' ' -f 1)"

echo "Checking that pre-mounted configuration and database files are not overwritten"
start_container "$EXISTING_CONTAINER" --volume "$existing_data:/app/data"
config_hash_after="$(sha256sum "$existing_data/appsettings.json" | cut -d ' ' -f 1)"
database_hash_after="$(sha256sum "$existing_data/vocabulary.db" | cut -d ' ' -f 1)"

test "$config_hash_before" = "$config_hash_after"
test "$database_hash_before" = "$database_hash_after"

echo "Container startup and persistence checks passed"
