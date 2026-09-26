#!/usr/bin/env bash
set -Eeuo pipefail

IMAGE_NAME="${1:-lexarbor:ci}"
# Optional. When given, the image must report exactly this version, which is how
# the release workflow's version argument is proven to reach the application.
EXPECTED_VERSION="${2:-}"
# Use "unknown" to assert that revision was not provided at build time.
EXPECTED_REVISION="${3:-}"
EXPECTED_CHANNEL="${4:-}"
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
      "http://127.0.0.1:${mapped_port}/health" >"$TEST_ROOT/health.json" 2>/dev/null; then
      jq --exit-status '. == {success:true,data:{status:"healthy"}}' \
        "$TEST_ROOT/health.json" >/dev/null
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

# From the startup log, not from /health. That endpoint is anonymous so the
# container probe can reach it without credentials, which makes everything it
# returns public, so the version is not among it.
check_reported_version() {
  local container_name="$1"
  local reported log_line
  # `|| true` because set -e aborts on a failed substitution, which would skip
  # the empty check below in exactly the case that check exists to report.
  log_line="$(docker logs "$container_name" 2>&1 |
    grep -m 1 -o 'Lexarbor starting, version [^[:space:]]*' || true)"
  reported="${log_line##* }"

  if [[ -z "$reported" ]]; then
    echo "Startup log did not report a version" >&2
    return 1
  fi

  if [[ -n "$EXPECTED_VERSION" && "$reported" != "$EXPECTED_VERSION" ]]; then
    echo "Expected version ${EXPECTED_VERSION} but the image reports ${reported}" >&2
    return 1
  fi

  echo "Image reports version ${reported}"

  local identity_line revision channel
  identity_line="$(docker logs "$container_name" 2>&1 |
    grep -m 1 -o 'Lexarbor build, channel [^[:space:]]*, revision [^[:space:]]*' || true)"
  if [[ -z "$identity_line" ]]; then
    echo "Startup log did not report build identity" >&2
    return 1
  fi
  revision="${identity_line##* }"
  channel="${identity_line#Lexarbor build, channel }"
  channel="${channel%%,*}"
  if [[ -n "$EXPECTED_REVISION" && "$revision" != "$EXPECTED_REVISION" ]]; then
    echo "Expected revision ${EXPECTED_REVISION} but the image reports ${revision}" >&2
    return 1
  fi
  if [[ -n "$EXPECTED_CHANNEL" && "$channel" != "$EXPECTED_CHANNEL" ]]; then
    echo "Expected channel ${EXPECTED_CHANNEL} but the image reports ${channel}" >&2
    return 1
  fi
  echo "Image reports channel ${channel}, revision ${revision}"
}

# The image runs as its own unprivileged user, which is what a named or anonymous
# volume is initialised for. A host bind mount keeps the host's ownership, so a
# bind-mounted run has to be the user owning that directory, exactly as
# scripts/start.sh does it. Passing --user for the unmounted case instead would
# fail, because the anonymous volume belongs to the image's user.
start_container() {
  local container_name="$1"
  shift
  docker run --detach \
    --name "$container_name" \
    --publish 127.0.0.1::5008 \
    --env APP_VERSION=9.9.9-runtime \
    --env APP_REVISION=ffffffffffffffffffffffffffffffffffffffff \
    --env APP_CHANNEL=edge \
    --env BuildRevision=ffffffffffffffffffffffffffffffffffffffff \
    --env BuildChannel=edge \
    "$@" \
    "$IMAGE_NAME" >/dev/null
  wait_for_health "$container_name"
}

start_bind_mounted_container() {
  local container_name="$1"
  local host_directory="$2"
  shift 2
  start_container "$container_name" \
    --user "$(id -u):$(id -g)" \
    --volume "$host_directory:/app/data" "$@"
}

check_runs_unprivileged() {
  local container_name="$1"
  local uid
  uid="$(docker exec "$container_name" id -u)"
  if [[ "$uid" == "0" ]]; then
    echo "Container ${container_name} is running as root" >&2
    return 1
  fi

  echo "Container ${container_name} runs as uid ${uid}"
}

check_healthcheck_reports_healthy() {
  local container_name="$1"
  local status
  # The image declares a HEALTHCHECK, so Docker tracks a status. Without a
  # declared check this stays "<no value>" forever, which is the regression this
  # catches: nothing else here would notice the instruction disappearing.
  for _ in $(seq 1 60); do
    status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$container_name")"
    if [[ "$status" == "healthy" ]]; then
      echo "Container ${container_name} reports healthy"
      return 0
    fi

    if [[ -z "$status" ]]; then
      echo "Image declares no HEALTHCHECK" >&2
      return 1
    fi
    sleep 2
  done

  docker inspect --format '{{json .State.Health}}' "$container_name" >&2 || true
  echo "Container ${container_name} never reported healthy" >&2
  return 1
}

# Lexarbor ships no vocabulary data, so a new database must start with an empty
# catalog. The runtime image has no sqlite3 to count rows with, so this asks the
# public API; the row counts themselves are covered by DatabaseInitializerTests.
check_catalog_is_empty() {
  local container_name="$1"
  local mapped_port response
  mapped_port="$(docker port "$container_name" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
  response="$(curl --fail --silent --show-error \
    "http://127.0.0.1:${mapped_port}/api/vocabulary-books/all")"

  if ! jq --exit-status '.success == true and .data.books == []' <<<"$response" >/dev/null; then
    echo "Expected an empty vocabulary catalog on a new database, got: ${response}" >&2
    return 1
  fi

  echo "Container ${container_name} starts with an empty vocabulary catalog"
}

echo "Checking startup without an explicit host mount"
start_container "$UNMOUNTED_CONTAINER"
check_reported_version "$UNMOUNTED_CONTAINER"
check_runs_unprivileged "$UNMOUNTED_CONTAINER"
check_healthcheck_reports_healthy "$UNMOUNTED_CONTAINER"
docker rm -f -v "$UNMOUNTED_CONTAINER" >/dev/null

fresh_data="$TEST_ROOT/fresh"
mkdir -p "$fresh_data"

echo "Checking first-start configuration and database creation"
start_bind_mounted_container "$FRESH_CONTAINER" "$fresh_data"
check_runs_unprivileged "$FRESH_CONTAINER"
test -s "$fresh_data/appsettings.json"
test -s "$fresh_data/vocabulary.db"
check_catalog_is_empty "$FRESH_CONTAINER"
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
start_bind_mounted_container "$EXISTING_CONTAINER" "$existing_data"
config_hash_after="$(sha256sum "$existing_data/appsettings.json" | cut -d ' ' -f 1)"
database_hash_after="$(sha256sum "$existing_data/vocabulary.db" | cut -d ' ' -f 1)"

test "$config_hash_before" = "$config_hash_after"
test "$database_hash_before" = "$database_hash_after"

echo "Container startup and persistence checks passed"
