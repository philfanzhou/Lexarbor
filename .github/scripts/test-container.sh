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
NAMED_CONTAINER="lexarbor-named-${RUN_SUFFIX}"
FAILURE_CONTAINER="lexarbor-failure-${RUN_SUFFIX}"
CODE_CONTAINER="lexarbor-code-${RUN_SUFFIX}"
PROVIDER_FAILURE_CONTAINER="lexarbor-provider-failure-${RUN_SUFFIX}"
NAMED_VOLUME="lexarbor-data-${RUN_SUFFIX}"
# The application and its database server share one disposable network; the
# server is reachable only from it. The password is synthetic and exists for
# this run only.
NETWORK_NAME="lexarbor-ci-net-${RUN_SUFFIX}"
POSTGRES_CONTAINER="lexarbor-ci-postgres-${RUN_SUFFIX}"
POSTGRES_IMAGE="${LEXARBOR_TEST_POSTGRES_IMAGE:-postgres:16-alpine}"
DB_PASSWORD="synthetic-db-$(od -An -N12 -tx1 /dev/urandom | tr -d ' \n')"
COMPOSE_PROJECT="lexarbor-ci-$(printf '%s' "$RUN_SUFFIX" | tr -c 'a-z0-9-' '-')"
TEST_ROOT="$(mktemp -d "${TMPDIR:-/tmp}/lexarbor-container.XXXXXX")"
readonly KEY_REPOSITORY_DIAGNOSTIC='The Data Protection key repository is unavailable or invalid'

cleanup() {
  docker rm -f -v \
    "$UNMOUNTED_CONTAINER" \
    "$FRESH_CONTAINER" \
    "$EXISTING_CONTAINER" \
    "$NAMED_CONTAINER" \
    "$FAILURE_CONTAINER" \
    "$CODE_CONTAINER" \
    "$PROVIDER_FAILURE_CONTAINER" \
    "$POSTGRES_CONTAINER" >/dev/null 2>&1 || true
  LEXARBOR_DB_PASSWORD="$DB_PASSWORD" docker compose -f scripts/compose.yaml -p "$COMPOSE_PROJECT" \
    down --volumes --remove-orphans >/dev/null 2>&1 || true
  docker volume rm "$NAMED_VOLUME" >/dev/null 2>&1 || true
  docker network rm "$NETWORK_NAME" >/dev/null 2>&1 || true
  case "$TEST_ROOT" in
    /tmp/*|"${TMPDIR:-/tmp}"/*) rm -rf -- "$TEST_ROOT" ;;
    *) echo "Refusing to remove unexpected temporary path: $TEST_ROOT" >&2 ;;
  esac
}
trap cleanup EXIT

# --- PostgreSQL --------------------------------------------------------------

start_database_server() {
  docker network create "$NETWORK_NAME" >/dev/null
  docker run --detach --name "$POSTGRES_CONTAINER" --network "$NETWORK_NAME" \
    --env POSTGRES_USER=lexarbor --env POSTGRES_PASSWORD="$DB_PASSWORD" --env POSTGRES_DB=postgres \
    "$POSTGRES_IMAGE" >/dev/null
  for _ in $(seq 1 60); do
    if docker exec "$POSTGRES_CONTAINER" pg_isready --username lexarbor --dbname postgres >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  docker logs "$POSTGRES_CONTAINER" >&2 || true
  echo "The test PostgreSQL server did not start" >&2
  return 1
}

# Runs SQL from stdin (or the given arguments) against one database and prints
# unaligned rows. Only synthetic rows are ever read back.
psql_db() {
  local database="$1"
  shift
  docker exec -i "$POSTGRES_CONTAINER" psql --no-psqlrc --quiet --tuples-only --no-align \
    --set ON_ERROR_STOP=1 --username lexarbor --dbname "$database" "$@"
}

create_database() {
  docker exec "$POSTGRES_CONTAINER" createdb --username lexarbor "$1"
}

clone_database() {
  docker exec "$POSTGRES_CONTAINER" createdb --username lexarbor --template "$1" "$2"
}

connection_string() {
  printf 'Host=%s;Database=%s;Username=lexarbor;Password=%s' "$POSTGRES_CONTAINER" "$1" "$DB_PASSWORD"
}

# --- Container helpers --------------------------------------------------------

wait_for_health() {
  local container_name="$1"
  local mapped_port
  mapped_port="$(docker port "$container_name" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"

  for _ in $(seq 1 60); do
    if curl --fail --silent --show-error \
      "http://127.0.0.1:${mapped_port}/health/ready" >"$TEST_ROOT/health.json" 2>/dev/null; then
      jq --exit-status '.status == "ready" and .phase == "completed" and .migrationStatus == "succeeded" and .databaseStatus == "reachable" and .errorCode == null' \
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
  local database="$2"
  shift 2
  docker run --detach \
    --name "$container_name" \
    --network "$NETWORK_NAME" \
    --publish 127.0.0.1::5008 \
    --env "ConnectionStrings__Default=$(connection_string "$database")" \
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
  local database="$2"
  local host_directory="$3"
  shift 3
  start_container "$container_name" "$database" \
    --user "$(id -u):$(id -g)" \
    --volume "$host_directory:/app/data" "$@"
}

# Waits for a container that must refuse to start and leaves its log in the
# given file.
expect_startup_failure() {
  local container_name="$1"
  local log_file="$2"
  for _ in $(seq 1 30); do
    if [[ "$(docker inspect --format '{{.State.Running}}' "$container_name")" != "true" ]]; then
      break
    fi
    sleep 1
  done
  if [[ "$(docker inspect --format '{{.State.Running}}' "$container_name")" != "false" ]]; then
    echo "Container $container_name was still running: $(docker inspect --format '{{json .State}}' "$container_name")" >&2
    docker logs --tail 20 "$container_name" >&2 || true
  fi
  test "$(docker inspect --format '{{.State.Running}}' "$container_name")" = false
  test "$(docker inspect --format '{{.State.ExitCode}}' "$container_name")" != 0
  docker logs "$container_name" >"$log_file" 2>&1
  docker rm -f "$container_name" >/dev/null
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

# The catalog key is generated here and only its hash is stored. Send the
# header on stdin so the key never appears in process arguments or logs.
check_catalog_is_empty() {
  local container_name="$1"
  local mapped_port response unauthenticated
  mapped_port="$(docker port "$container_name" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
  unauthenticated="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
    "http://127.0.0.1:${mapped_port}/api/vocabulary-books/all")"
  test "$unauthenticated" = 401
  response="$(printf 'X-Api-Key: %s\n' "$catalog_key" | curl --fail --silent --show-error --header @- \
    "http://127.0.0.1:${mapped_port}/api/vocabulary-books/all")"
  jq --exit-status '.success == true and .data.books == []' <<<"$response" >/dev/null
  echo "Authenticated catalog is empty in ${container_name}"
}

# Inspect metadata only; never print the root-key file's contents. The data
# directory must be owner-only: the ServiceMantle root-key source refuses a
# key file under a directory with any group or other access bits, so the image
# ships /app/data at 0700 and bind mounts must match.
check_root_key_file() {
  local container_name="$1"
  docker exec "$container_name" sh -ec '
    key=/app/data/data-protection-root-key
    test -f "$key"
    test "$(stat -c %a "$key")" = 600
    test "$(stat -c %u "$key")" = "$(id -u)"
    test "$(stat -c %a /app/data)" = 700
    test ! -L "$key"
    test ! -L /app/data
  '
}

# The image keeps no database: /app/data holds only the persistent
# configuration and the root-key file.
check_no_database_file() {
  local container_name="$1"
  docker exec "$container_name" sh -ec 'test -z "$(find /app/data -name "*.db*" -print -quit)"'
}

check_key_startup_rejected() {
  local database="$1"
  local host_directory="$2"
  docker run --detach --name "$FAILURE_CONTAINER" \
    --network "$NETWORK_NAME" \
    --env "ConnectionStrings__Default=$(connection_string "$database")" \
    --user "$(id -u):$(id -g)" \
    --volume "$host_directory:/app/data" \
    "$IMAGE_NAME" >/dev/null
  expect_startup_failure "$FAILURE_CONTAINER" "$TEST_ROOT/rejected.log"
  grep -q "$KEY_REPOSITORY_DIAGNOSTIC" "$TEST_ROOT/rejected.log"
  if grep -q 'synthetic-secret-marker' "$TEST_ROOT/rejected.log"; then
    echo "Key material appeared in startup diagnostics" >&2
    return 1
  fi
  if grep -q 'data-protection-root-key' "$TEST_ROOT/rejected.log"; then
    echo "The root-key file name appeared in startup diagnostics" >&2
    return 1
  fi
}

# The method probe was deleted with the password form: the route now answers
# like any unknown /admin/* route, anonymous 401 with the auth envelope.
check_method_route_deleted() {
  local container_name="$1" mapped_port status
  mapped_port="$(docker port "$container_name" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
  status="$(curl --silent --show-error --output "$TEST_ROOT/method-body" --write-out '%{http_code}' \
    "http://127.0.0.1:${mapped_port}/admin/auth/method")"
  test "$status" = 401
  jq --exit-status '. == {success:false,message:"Authentication is required."}' "$TEST_ROOT/method-body" >/dev/null
}

# Every row of the given tables, ordered by the first column.
snapshot_tables() {
  local database="$1"
  shift
  local table
  for table in "$@"; do
    printf '== %s\n' "$table"
    psql_db "$database" --command "SELECT * FROM ${table} ORDER BY 1"
  done
}

bash .github/scripts/test-start-configuration.sh

start_database_server

echo "Checking a missing connection string refuses startup"
docker run --detach --name "$FAILURE_CONTAINER" "$IMAGE_NAME" >/dev/null
expect_startup_failure "$FAILURE_CONTAINER" "$TEST_ROOT/missing-connection.log"
grep -q 'ConnectionStrings:Default is required and must be a PostgreSQL connection string.' "$TEST_ROOT/missing-connection.log"

echo "Checking a SQLite connection string refuses startup without echoing it"
docker run --detach --name "$FAILURE_CONTAINER" \
  --env 'ConnectionStrings__Default=Data Source=/app/data/synthetic-sensitive-marker.db' \
  "$IMAGE_NAME" >/dev/null
expect_startup_failure "$FAILURE_CONTAINER" "$TEST_ROOT/sqlite-connection.log"
grep -q 'ConnectionStrings:Default is a SQLite connection string' "$TEST_ROOT/sqlite-connection.log"
! grep -q 'synthetic-sensitive-marker' "$TEST_ROOT/sqlite-connection.log"

echo "Checking an unreachable database refuses startup without echoing the connection string"
docker run --detach --name "$FAILURE_CONTAINER" --network "$NETWORK_NAME" \
  --env 'ConnectionStrings__Default=Host=lexarbor-no-such-host;Database=lexarbor;Username=lexarbor;Password=synthetic-sensitive-marker;Timeout=3' \
  "$IMAGE_NAME" >/dev/null
expect_startup_failure "$FAILURE_CONTAINER" "$TEST_ROOT/unreachable.log"
grep -q 'The database startup migration was refused' "$TEST_ROOT/unreachable.log"
! grep -q 'synthetic-sensitive-marker' "$TEST_ROOT/unreachable.log"
! grep -q 'lexarbor-no-such-host' "$TEST_ROOT/unreachable.log"

echo "Checking startup without an explicit host mount creates the database"
start_container "$UNMOUNTED_CONTAINER" lexarbor_unmounted
check_reported_version "$UNMOUNTED_CONTAINER"
check_runs_unprivileged "$UNMOUNTED_CONTAINER"
check_root_key_file "$UNMOUNTED_CONTAINER"
check_no_database_file "$UNMOUNTED_CONTAINER"
check_method_route_deleted "$UNMOUNTED_CONTAINER"
check_healthcheck_reports_healthy "$UNMOUNTED_CONTAINER"
test "$(psql_db lexarbor_unmounted --command 'SELECT count(*) FROM "__EFMigrationsHistory"')" -gt 0
docker rm -f -v "$UNMOUNTED_CONTAINER" >/dev/null

echo "Checking explicit Code mode with missing configuration fails safely"
# The docker bridge networks (172.16.0.0/12) are trusted for forwarded headers
# so the logout smoke below can speak to the container the way production
# traffic arrives: behind an SSL-terminating proxy. The official antiforgery
# boundary added with B6 (CookieSecurePolicy.Always) rejects a plain-HTTP
# direct hit.
create_database lexarbor_code
start_container "$CODE_CONTAINER" lexarbor_code --env AdminAuthentication__Provider=OidcCode \
  --env Network__TrustedNetworks__0=172.16.0.0/12
check_method_route_deleted "$CODE_CONTAINER"
code_port="$(docker port "$CODE_CONTAINER" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
code_status="$(curl --silent --show-error --dump-header "$TEST_ROOT/code-headers" --output "$TEST_ROOT/code-body" --write-out '%{http_code}' "http://127.0.0.1:${code_port}/admin/auth/start")"
test "$code_status" = 503
jq --exit-status '. == {success:false,message:"Hosted authentication is not configured."}' "$TEST_ROOT/code-body" >/dev/null
grep -qi '^Cache-Control: no-store' "$TEST_ROOT/code-headers"
! grep -qi '^Set-Cookie:' "$TEST_ROOT/code-headers"
# The deleted password route answers like any unknown admin route: anonymous 401.
password_status="$(curl --silent --show-error --output "$TEST_ROOT/code-password" --write-out '%{http_code}' --request POST --header 'Content-Type: application/json' --data '{broken' "http://127.0.0.1:${code_port}/admin/auth/login")"
test "$password_status" = 401
jq --exit-status '. == {success:false,message:"Authentication is required."}' "$TEST_ROOT/code-password" >/dev/null
echo "Checking Code-mode logout smoke: sessionless local-only logout and failed-return redirect"
# Since the B6 antiforgery migration the hosted-logout route only accepts an
# unsafe POST that carries the official token model: fetch a token and its
# browser cookie from the public csrf endpoint first, then echo the token in
# X-SignaCore-CSRF. The requests carry the proxy-forwarded scheme the container
# is configured to trust above (as an SSL-terminating proxy presents it), and
# the cookie is replayed as a literal header value because the official
# antiforgery cookie is always Secure, which a curl cookie jar would refuse to
# send over plain loopback HTTP.
csrf_status="$(curl --silent --show-error --dump-header "$TEST_ROOT/code-csrf-headers" --output "$TEST_ROOT/code-csrf" --write-out '%{http_code}' --header "X-Forwarded-Proto: https" --header "X-Forwarded-For: 203.0.113.7" "http://127.0.0.1:${code_port}/admin/auth/csrf")"
test "$csrf_status" = 200
csrf_token="$(jq --raw-output '.token' "$TEST_ROOT/code-csrf")"
csrf_cookie="$(awk 'tolower($1)=="set-cookie:"{print $2}' "$TEST_ROOT/code-csrf-headers" | head -n 1)"
csrf_cookie="${csrf_cookie%;}"
test -n "$csrf_token"
test -n "$csrf_cookie"
logout_status="$(curl --silent --show-error --dump-header "$TEST_ROOT/code-logout-headers" --output "$TEST_ROOT/code-logout" --write-out '%{http_code}' --request POST --header "X-Forwarded-Proto: https" --header "X-Forwarded-For: 203.0.113.7" --header "X-SignaCore-CSRF: ${csrf_token}" --cookie "${csrf_cookie}" "http://127.0.0.1:${code_port}/admin/auth/logout")"
test "$logout_status" = 200
jq --exit-status '. == {success:true}' "$TEST_ROOT/code-logout" >/dev/null
grep -qi '^Cache-Control: no-store' "$TEST_ROOT/code-logout-headers"
return_status="$(curl --silent --show-error --dump-header "$TEST_ROOT/code-return-headers" --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:${code_port}/admin/auth/logout/return?state=sensitive-state-marker")"
test "$return_status" = 302
grep -qi '^Location: /#/login?reason=logout_failed' "$TEST_ROOT/code-return-headers"
grep -qi '^Cache-Control: no-store' "$TEST_ROOT/code-return-headers"
grep -qi '^Referrer-Policy: no-referrer' "$TEST_ROOT/code-return-headers"
! grep -q 'sensitive-state-marker' "$TEST_ROOT/code-return-headers"
docker rm -f -v "$CODE_CONTAINER" >/dev/null

echo "Checking a removed password provider value refuses startup"
# A database of its own: another container's key rows would not match this
# container's new root key, and the key check would fail first.
docker run --detach --name "$PROVIDER_FAILURE_CONTAINER" \
  --network "$NETWORK_NAME" \
  --env "ConnectionStrings__Default=$(connection_string lexarbor_provider)" \
  --env AdminAuthentication__Provider=Oidc \
  "$IMAGE_NAME" >/dev/null
expect_startup_failure "$PROVIDER_FAILURE_CONTAINER" "$TEST_ROOT/provider-rejected.log"
grep -q 'AdminAuthentication:Provider' "$TEST_ROOT/provider-rejected.log"
grep -q 'HostedLoginReleaseNotes' "$TEST_ROOT/provider-rejected.log"

echo "Checking named-volume key storage as the image user"
docker volume create "$NAMED_VOLUME" >/dev/null
start_container "$NAMED_CONTAINER" lexarbor_named --volume "$NAMED_VOLUME:/app/data"
check_runs_unprivileged "$NAMED_CONTAINER"
check_root_key_file "$NAMED_CONTAINER"
docker rm -f "$NAMED_CONTAINER" >/dev/null

fresh_data="$TEST_ROOT/fresh"
mkdir -p "$fresh_data"
chmod 700 "$fresh_data"

echo "Checking first-start configuration and database creation"
start_bind_mounted_container "$FRESH_CONTAINER" lexarbor_fresh "$fresh_data"
check_runs_unprivileged "$FRESH_CONTAINER"
check_root_key_file "$FRESH_CONTAINER"
check_no_database_file "$FRESH_CONTAINER"
test -s "$fresh_data/appsettings.json"
test "$(docker exec "$FRESH_CONTAINER" stat -c '%a' /app/data/appsettings.json)" = 600
cmp --silent src/Lexarbor.Host/appsettings.json "$fresh_data/appsettings.json"
! grep -q 'ConnectionStrings' "$fresh_data/appsettings.json"
docker exec "$FRESH_CONTAINER" sh -c 'sha256sum /app/data/data-protection-root-key' >"$TEST_ROOT/original-key-hashes"

echo "Checking new-container login limit defaults to disabled"
jq --exit-status '.RateLimits.AdminLogin.Enabled == false and (.RateLimits | has("PublicApi") | not)' \
  "$fresh_data/appsettings.json" >/dev/null
fresh_port="$(docker port "$FRESH_CONTAINER" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
for _ in $(seq 1 11); do
  probe_status="$(curl --silent --show-error --dump-header "$TEST_ROOT/rate-headers" --output /dev/null --write-out '%{http_code}' \
    "http://127.0.0.1:${fresh_port}/admin/auth/start")"
  test "$probe_status" != 429
  ! grep -qi '^Retry-After:' "$TEST_ROOT/rate-headers"
done
docker rm -f "$FRESH_CONTAINER" >/dev/null
catalog_key="lxk_$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
catalog_hash="$(printf '%s' "$catalog_key" | sha256sum | cut -d ' ' -f 1)"
psql_db lexarbor_fresh <<SQL
INSERT INTO business_api_key (id, secret_hash, caller_id, catalog, meaning_submissions, created_at_unix_ms)
VALUES ('$(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen | tr 'A-Z' 'a-z')', '${catalog_hash}', 'container-smoke', true, false, 1);
SQL
start_bind_mounted_container "$FRESH_CONTAINER" lexarbor_fresh "$fresh_data"
check_catalog_is_empty "$FRESH_CONTAINER"
docker rm -f "$FRESH_CONTAINER" >/dev/null

echo "Checking specific-audio edits and enrichment discovery/service state survive container recreation"
invalidation_data="$TEST_ROOT/audio-invalidation"
mkdir -p "$invalidation_data"
chmod 700 "$invalidation_data"
cp "$fresh_data/appsettings.json" "$fresh_data/data-protection-root-key" "$invalidation_data/"
cp "$invalidation_data/appsettings.json" "$TEST_ROOT/invalidation-settings-before.json"
clone_database lexarbor_fresh lexarbor_invalidation
zero_hash="$(printf '0%.0s' $(seq 1 64))"
plan_json='{"SpokenText":"Cleanup probe","PronunciationHint":"Synthetic saved plan","Phonetic":null,"PartsOfSpeech":[],"InferredEntryKind":null,"Preparation":null}'
psql_db lexarbor_invalidation <<SQL
INSERT INTO vocabulary_book (id, book_name, display_order, status, created_at, updated_at) VALUES ('cleanup-book', 'Cleanup probe', 0, true, '2026-01-01', '2026-01-01');
INSERT INTO vocabulary (id, word, created_at, updated_at) VALUES ('cleanup-word', 'Cleanup probe', '2026-01-01', '2026-01-01');
INSERT INTO vocabulary_meaning (id, vocabulary_id, book_id, meaning, created_at, updated_at) VALUES ('cleanup-meaning', 'cleanup-word', 'cleanup-book', 'Synthetic cleanup meaning', '2026-01-01', '2026-01-01');
DO \$\$
DECLARE i int; asset text; object text; state text;
BEGIN
  FOR i IN 1..3 LOOP
    asset := 'cleanup-' || i;
    object := 'audio/' || lpad(to_hex(i), 32, '0') || '.mp3';
    state := (ARRAY['Waiting', 'Claimed', 'Observed'])[i];
    INSERT INTO audio_asset (id, bucket, object_key, sha256, byte_length, content_type, created_at_unix_ms, first_published_at_unix_ms)
      VALUES (asset, 'test-audio', object, '${zero_hash}', 128, 'audio/mpeg', 1, 2);
    INSERT INTO vocabulary_audio_binding (id, word_id, meaning_id, locale, asset_id, source_revision)
      VALUES (asset, 'cleanup-word', 'cleanup-meaning', 'en-GB', asset, '${zero_hash}');
    INSERT INTO audio_upload_operation (idempotency_key, request_fingerprint, word_id, meaning_id, locale, source_revision, asset_id, state,
        created_at_unix_ms, updated_at_unix_ms, upload_lease_fence, receipt_status_code, receipt_asset_id, receipt_source_revision,
        completed_at_unix_ms, cleanup_lease_fence, cleanup_attempts)
      VALUES ('00000000-0000-0000-0000-' || lpad(to_hex(i), 12, '0'), '${zero_hash}', 'cleanup-word', 'cleanup-meaning', 'en-GB', '${zero_hash}', asset,
        'Succeeded', 1, 2, 1, 200, asset, '${zero_hash}', 2, 0, 0);
    INSERT INTO audio_invalidation (asset_id, bucket, object_key, word_id, meaning_id, locale, authorization_epoch, reason, state,
        created_at_unix_ms, updated_at_unix_ms, lease_fence, attempts, next_attempt_at_unix_ms)
      VALUES (asset, 'test-audio', object, 'cleanup-word', 'cleanup-meaning', 'en-GB', 7, 'specific_phonetic_changed', 'Waiting', 3, 3, 0, 0, 4);
    DELETE FROM vocabulary_audio_binding WHERE asset_id = asset;
    IF state <> 'Waiting' THEN
      UPDATE audio_invalidation SET state = 'Claimed', delete_started_at_unix_ms = 4, updated_at_unix_ms = 4, lease_owner = 'interrupted',
        lease_fence = 2, lease_expires_at_unix_ms = 5, attempts = 6 WHERE asset_id = asset;
    END IF;
    IF state = 'Observed' THEN
      UPDATE audio_invalidation SET state = 'Observed', observed_at_unix_ms = 5, updated_at_unix_ms = 5, lease_owner = NULL,
        lease_expires_at_unix_ms = NULL, next_attempt_at_unix_ms = 6 WHERE asset_id = asset;
    END IF;
  END LOOP;
END \$\$;
UPDATE vocabulary_meaning SET phonetic_uk = 'edited-specific' WHERE id = 'cleanup-meaning';
INSERT INTO meaning_phonetic_epoch (meaning_id, word_id, locale, epoch) VALUES ('cleanup-meaning', 'cleanup-word', 'en-GB', 7);
UPDATE audio_upload_operation SET meaning_phonetic_epoch = 6;
INSERT INTO enrichment_target (id, word_id, meaning_id, locale, step, current_round, epoch, deleted) VALUES
  ('edit-Prepare', 'cleanup-word', 'cleanup-meaning', 'en-GB', 'Prepare', 1, 7, false),
  ('edit-Audio', 'cleanup-word', 'cleanup-meaning', 'en-GB', 'Audio', 1, 7, false);
INSERT INTO enrichment_round (target_id, number, state, input_revision, source_revision, configuration_version, failure_budget,
    request_count, consumed_failures, lease_fence, created_at_unix_ms, updated_at_unix_ms) VALUES
  ('edit-Prepare', 1, 'Pending', '${zero_hash}', '${zero_hash}', 'persisted-edit-config', 5, 0, 0, 0, 3, 3),
  ('edit-Audio', 1, 'Pending', '${zero_hash}', '${zero_hash}', 'persisted-edit-config', 5, 0, 0, 0, 3, 3);
-- Synthetic persisted outcomes: this checks exact retention, not provider execution.
INSERT INTO enrichment_target (id, word_id, meaning_id, locale, step, current_round, epoch, deleted) VALUES
  ('saved-prepare', 'cleanup-word', NULL, 'en-GB', 'Prepare', 1, 1, false),
  ('throttled-audio', 'cleanup-word', NULL, 'en-US', 'Audio', 1, 1, false),
  ('exhausted-audio', 'cleanup-word', 'cleanup-meaning', 'en-US', 'Audio', 1, 1, false);
INSERT INTO enrichment_round (target_id, number, state, input_revision, source_revision, configuration_version, failure_budget,
    request_count, consumed_failures, plan_json, wait_reason, next_attempt_at_unix_ms, lease_fence, created_at_unix_ms, updated_at_unix_ms) VALUES
  ('saved-prepare', 1, 'Succeeded', '${zero_hash}', '${zero_hash}', 'persisted-step-config', 5, 1, 0, '${plan_json}', NULL, NULL, 0, 3, 9),
  ('throttled-audio', 1, 'Waiting', '${zero_hash}', '${zero_hash}', 'persisted-step-config', 5, 7, 0, '${plan_json}', 'Throttled', 123456, 0, 3, 9),
  ('exhausted-audio', 1, 'Failed', '${zero_hash}', '${zero_hash}', 'persisted-step-config', 5, 5, 5, '${plan_json}', NULL, NULL, 0, 3, 9);
INSERT INTO enrichment_call (id, target_id, round, service, result, error_code, started_at_unix_ms, finished_at_unix_ms)
  SELECT 'saved-prepare-0', 'saved-prepare', 1, 'text', 'Succeeded', NULL, 3, 9
  UNION ALL SELECT 'throttled-audio-' || i, 'throttled-audio', 1, 'tts', 'Throttled', 'tts.rate_limited', 3, 9 FROM generate_series(0, 6) AS i
  UNION ALL SELECT 'exhausted-audio-' || i, 'exhausted-audio', 1, 'tts', 'TransientFailure', 'tts.service_unavailable', 3, 9 FROM generate_series(0, 4) AS i;
INSERT INTO enrichment_wait (id, target_id, round, reason, error_code, recorded_at_unix_ms, next_attempt_at_unix_ms)
  VALUES ('saved-wait', 'throttled-audio', 1, 'Throttled', 'tts.rate_limited', 9, 123456);
INSERT INTO enrichment_discovery (id, cursor, upper_bound) VALUES ('words', 'cleanup-word', 'retained-word'), ('meanings', 'cleanup-meaning', 'retained-meaning');
INSERT INTO enrichment_service (id, configuration_version, consecutive_failures, throttle_seconds, paused, blocked, pause_seconds,
    next_attempt_at_unix_ms, lease_id, lease_expires_at_unix_ms)
  VALUES ('synthetic-tts-service', 'synthetic-config', 5, 120, true, false, 600, 123456, 'interrupted-probe', 234567);
INSERT INTO enrichment_service_probe (id, service_id, result, request_made, started_at_unix_ms)
  VALUES ('interrupted-probe', 'synthetic-tts-service', 'Started', true, 12);
SQL
invalidation_tables=(audio_asset audio_upload_operation audio_invalidation meaning_phonetic_epoch enrichment_target enrichment_round
  enrichment_call enrichment_wait enrichment_discovery enrichment_service enrichment_service_probe)
snapshot_tables lexarbor_invalidation "${invalidation_tables[@]}" >"$TEST_ROOT/invalidation-before.txt"
for recreation in 1 2; do
  start_bind_mounted_container "$EXISTING_CONTAINER" lexarbor_invalidation "$invalidation_data"
  docker rm -f "$EXISTING_CONTAINER" >/dev/null
  cmp "$TEST_ROOT/invalidation-settings-before.json" "$invalidation_data/appsettings.json"
  snapshot_tables lexarbor_invalidation "${invalidation_tables[@]}" >"$TEST_ROOT/invalidation-after.txt"
  cmp "$TEST_ROOT/invalidation-before.txt" "$TEST_ROOT/invalidation-after.txt"
  test "$(psql_db lexarbor_invalidation --command 'SELECT count(*) FROM vocabulary_audio_binding')" = 0
  test "$(psql_db lexarbor_invalidation --command 'SELECT count(*) FROM audio_invalidation WHERE delete_started_at_unix_ms IS NOT NULL')" = 2
  test "$(psql_db lexarbor_invalidation --command "SELECT count(*) FROM enrichment_round WHERE state = 'Pending'")" = 2
  test "$(psql_db lexarbor_invalidation --command 'SELECT count(*) FROM enrichment_call')" = 13
done

echo "Checking immutable review outcomes and automatic retry state survive container recreation"
review_data="$TEST_ROOT/submission-review"
mkdir -p "$review_data"
chmod 700 "$review_data"
cp "$fresh_data/appsettings.json" "$fresh_data/data-protection-root-key" "$review_data/"
clone_database lexarbor_fresh lexarbor_review
validated_review='{"AttemptId":"staged-attempt","SubmissionId":"pending-auto","Verdict":"accept","PartOfSpeech":null,"Meaning":null,"PhoneticUk":null,"PhoneticUs":null,"Comment":null,"Model":"synthetic-model","ReviewedAt":"2026-11-02T00:00:00+00:00"}'
psql_db lexarbor_review <<SQL
INSERT INTO meaning_submission (id, source, word, part_of_speech, meaning, example, status, created_at_unix_ms, updated_at_unix_ms,
    reviewed_at_unix_ms, review_comment, reviewer_kind, ai_review_verdict)
  VALUES ('retained-submission', 'synthetic-client', 'Mispelled', 'n.', 'original meaning', 'original example', 2, 1, 2, 2,
    'Synthetic rejection', 'ai', 'reject');
INSERT INTO meaning_submission_ai_review (id, attempt_id, submission_id, verdict, part_of_speech, meaning, phonetic_uk, phonetic_us,
    comment, model, reviewed_at_unix_ms, applied)
  VALUES ('retained-review', 'retained-attempt', 'retained-submission', 'reject', 'n.', 'proposed meaning', '/uk/', '/us/',
    'Synthetic rejection', 'synthetic-actual-model', 2, true);
INSERT INTO meaning_submission (id, source, word, meaning, status, created_at_unix_ms, updated_at_unix_ms)
  VALUES ('pending-auto', 'synthetic-client', 'Word', 'pending original', 0, 3, 3);
INSERT INTO meaning_submission_auto_review (submission_id, attempt_id, owner, epoch, lease_until_unix_ms, next_attempt_at_unix_ms,
    consecutive_failures, last_failure_code, last_failure_at_unix_ms, validated_review_json)
  VALUES ('pending-auto', 'staged-attempt', 'previous-process', 4, 1000, 2000, 12, 'result_commit_failed', 3, '${validated_review}');
INSERT INTO meaning_submission_review_check (record_id, checked_at_unix_ms) VALUES ('retained-review', 4);
SQL
review_tables=(meaning_submission meaning_submission_ai_review meaning_submission_auto_review meaning_submission_review_check)
snapshot_tables lexarbor_review "${review_tables[@]}" >"$TEST_ROOT/review-before.txt"
for recreation in 1 2; do
  start_bind_mounted_container "$EXISTING_CONTAINER" lexarbor_review "$review_data"
  docker rm -f "$EXISTING_CONTAINER" >/dev/null
  snapshot_tables lexarbor_review "${review_tables[@]}" >"$TEST_ROOT/review-after.txt"
  cmp "$TEST_ROOT/review-before.txt" "$TEST_ROOT/review-after.txt"
  test "$(psql_db lexarbor_review --command 'SELECT count(*) FROM vocabulary')" = 0
done

# Preserve both explicit login values and ignore obsolete business settings.
# Higher-priority overrides must leave the operator file intact.
echo "Checking persisted request switches and environment/argument precedence"
rate_data="$TEST_ROOT/rate-limits"
mkdir -p "$rate_data"
chmod 700 "$rate_data"
for probe_mode in file environment arguments; do
  for login_enabled in true false; do
    if [[ "$login_enabled" == true ]]; then opposite=false; else opposite=true; fi
    jq --argjson login "$login_enabled" \
      '.RateLimits.AdminLogin = {Enabled:$login, PermitLimit:1, WindowSeconds:300} | .RateLimits.PublicApi = {Enabled:true, PermitLimit:"obsolete", WindowSeconds:0}' \
      src/Lexarbor.Host/appsettings.json >"$rate_data/appsettings.json"
    cp "$rate_data/appsettings.json" "$TEST_ROOT/rate-before.json"
    probe_env=()
    probe_args=()
    if [[ "$probe_mode" != file ]]; then
      probe_env=(--env "RateLimits__AdminLogin__Enabled=$opposite" --env "RateLimits__PublicApi__Enabled=true")
    fi
    if [[ "$probe_mode" == environment ]]; then
      expected_login="$opposite"
    else
      expected_login="$login_enabled"
    fi
    if [[ "$probe_mode" == arguments ]]; then
      probe_args=(--RateLimits:AdminLogin:Enabled "$login_enabled" --RateLimits:PublicApi:Enabled true)
    fi
    docker run --detach --name "$EXISTING_CONTAINER" \
      --network "$NETWORK_NAME" --env "ConnectionStrings__Default=$(connection_string lexarbor_rate)" \
      --user "$(id -u):$(id -g)" --volume "$rate_data:/app/data" \
      --publish 127.0.0.1::5008 ${probe_env[@]+"${probe_env[@]}"} "$IMAGE_NAME" ${probe_args[@]+"${probe_args[@]}"} >/dev/null
    wait_for_health "$EXISTING_CONTAINER"
    probe_port="$(docker port "$EXISTING_CONTAINER" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
    for attempt in 1 2; do
      probe_status="$(curl --silent --show-error --dump-header "$TEST_ROOT/rate-headers" --output /dev/null --write-out '%{http_code}' \
        "http://127.0.0.1:${probe_port}/admin/auth/start")"
      if [[ "$expected_login" == true && "$attempt" == 2 ]]; then
        test "$probe_status" = 429
        grep -qi '^Retry-After:' "$TEST_ROOT/rate-headers"
      else
        test "$probe_status" != 429
        ! grep -qi '^Retry-After:' "$TEST_ROOT/rate-headers"
      fi
      probe_status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
        "http://127.0.0.1:${probe_port}/api/vocabulary-books/all")"
      test "$probe_status" = 401
    done
    cmp --silent "$TEST_ROOT/rate-before.json" "$rate_data/appsettings.json"
    docker rm -f "$EXISTING_CONTAINER" >/dev/null
  done
done

echo "Checking keys survive container recreation with the same data mount"
start_bind_mounted_container "$FRESH_CONTAINER" lexarbor_fresh "$fresh_data"
check_runs_unprivileged "$FRESH_CONTAINER"
check_root_key_file "$FRESH_CONTAINER"
docker exec "$FRESH_CONTAINER" sh -c 'sha256sum /app/data/data-protection-root-key' >"$TEST_ROOT/restarted-key-hashes"
cmp --silent "$TEST_ROOT/original-key-hashes" "$TEST_ROOT/restarted-key-hashes"
check_catalog_is_empty "$FRESH_CONTAINER"
cmp --silent src/Lexarbor.Host/appsettings.json "$fresh_data/appsettings.json"
docker rm -f "$FRESH_CONTAINER" >/dev/null

echo "Checking a read-only data directory with its files in place still serves"
# The database is on the server, so a read-only /app/data that already holds
# the configuration and root-key files has nothing left to write.
start_container "$EXISTING_CONTAINER" lexarbor_fresh \
  --user "$(id -u):$(id -g)" --volume "$fresh_data:/app/data:ro"
check_catalog_is_empty "$EXISTING_CONTAINER"
docker rm -f "$EXISTING_CONTAINER" >/dev/null

echo "Checking a wrong root key fails startup without logging key material"
wrong_key_data="$TEST_ROOT/wrong-key"
mkdir -p "$wrong_key_data"
chmod 700 "$wrong_key_data"
cp "$fresh_data/appsettings.json" "$wrong_key_data/appsettings.json"
# A fresh random root key does not match the database's existing key rows.
head -c 32 /dev/urandom | base64 >"$wrong_key_data/data-protection-root-key"
chmod 600 "$wrong_key_data/data-protection-root-key"
check_key_startup_rejected lexarbor_fresh "$wrong_key_data"

echo "Checking a loose root-key file fails startup"
loose_key_data="$TEST_ROOT/loose-key"
mkdir -p "$loose_key_data"
chmod 700 "$loose_key_data"
cp "$fresh_data/appsettings.json" "$loose_key_data/appsettings.json"
cp "$fresh_data/data-protection-root-key" "$loose_key_data/data-protection-root-key"
chmod 644 "$loose_key_data/data-protection-root-key"
check_key_startup_rejected lexarbor_fresh "$loose_key_data"

existing_data="$TEST_ROOT/existing"
mkdir -p "$existing_data"
chmod 700 "$existing_data"
cat > "$existing_data/appsettings.json" <<'JSON'
{
  "Database": {
    "InitializeOnStartup": false
  },
  "PersistenceProbe": "preserve-existing-configuration"
}
JSON
create_database lexarbor_uninitialized
config_hash_before="$(sha256sum "$existing_data/appsettings.json" | cut -d ' ' -f 1)"

echo "Checking that a pre-mounted configuration is kept and disabled initialization leaves the database alone"
# Readiness honestly reports not-ready for the unmigrated database, so this
# container never turns healthy by design; verify it serves liveness instead,
# that the pre-mounted file was not rewritten, and that the database was not
# migrated. The key repository probe stays silent here: the schema was never
# verified, and an unverified schema is the operator's responsibility.
docker run --detach \
  --name "$EXISTING_CONTAINER" \
  --network "$NETWORK_NAME" \
  --env "ConnectionStrings__Default=$(connection_string lexarbor_uninitialized)" \
  --user "$(id -u):$(id -g)" \
  --volume "$existing_data:/app/data" \
  --publish 127.0.0.1::5008 \
  "$IMAGE_NAME" >/dev/null
existing_port="$(docker port "$EXISTING_CONTAINER" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
for _ in $(seq 1 30); do
  if curl --fail --silent --show-error "http://127.0.0.1:${existing_port}/health/live" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
# The ready answer is an expected 503, so fetch it without --fail.
curl --silent --show-error "http://127.0.0.1:${existing_port}/health/ready" >"$TEST_ROOT/existing-health.json"
jq --exit-status '.status == "not_ready" and .migrationStatus == "notStarted"' "$TEST_ROOT/existing-health.json" >/dev/null
config_hash_after="$(sha256sum "$existing_data/appsettings.json" | cut -d ' ' -f 1)"
test "$config_hash_before" = "$config_hash_after"
test "$(psql_db lexarbor_uninitialized --command "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'")" = 0
docker rm -f "$EXISTING_CONTAINER" >/dev/null

echo "Checking the Compose example starts the application with its database"
export LEXARBOR_DB_PASSWORD="$DB_PASSWORD"
LEXARBOR_IMAGE="$IMAGE_NAME" LEXARBOR_PORT="127.0.0.1:0" LEXARBOR_POSTGRES_IMAGE="$POSTGRES_IMAGE" \
  docker compose -f scripts/compose.yaml -p "$COMPOSE_PROJECT" up --detach --wait --wait-timeout 120 postgres >/dev/null
LEXARBOR_IMAGE="$IMAGE_NAME" LEXARBOR_PORT="127.0.0.1:0" LEXARBOR_POSTGRES_IMAGE="$POSTGRES_IMAGE" \
  docker compose -f scripts/compose.yaml -p "$COMPOSE_PROJECT" up --detach lexarbor >/dev/null
compose_container="$(docker compose -f scripts/compose.yaml -p "$COMPOSE_PROJECT" ps --quiet lexarbor)"
wait_for_health "$compose_container"
LEXARBOR_IMAGE="$IMAGE_NAME" docker compose -f scripts/compose.yaml -p "$COMPOSE_PROJECT" restart lexarbor >/dev/null
wait_for_health "$compose_container"
check_root_key_file "$compose_container"
check_no_database_file "$compose_container"
docker compose -f scripts/compose.yaml -p "$COMPOSE_PROJECT" down --volumes >/dev/null 2>&1
unset LEXARBOR_DB_PASSWORD

echo "Container startup and persistence checks passed"
