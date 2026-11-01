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
TEST_ROOT="$(mktemp -d)"
readonly KEY_REPOSITORY_DIAGNOSTIC='The Data Protection key repository is unavailable or invalid'

cleanup() {
  docker rm -f -v \
    "$UNMOUNTED_CONTAINER" \
    "$FRESH_CONTAINER" \
    "$EXISTING_CONTAINER" \
    "$NAMED_CONTAINER" \
    "$FAILURE_CONTAINER" \
    "$CODE_CONTAINER" \
    "$PROVIDER_FAILURE_CONTAINER" >/dev/null 2>&1 || true
  docker volume rm "$NAMED_VOLUME" >/dev/null 2>&1 || true
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

check_key_startup_rejected() {
  local host_directory="$1"
  local mount_options="${2:-rw}"
  docker run --detach --name "$FAILURE_CONTAINER" \
    --user "$(id -u):$(id -g)" \
    --volume "$host_directory:/app/data:$mount_options" \
    "$IMAGE_NAME" >/dev/null
  for _ in $(seq 1 30); do
    if [[ "$(docker inspect --format '{{.State.Running}}' "$FAILURE_CONTAINER")" != "true" ]]; then
      break
    fi
    sleep 1
  done
  if [[ "$(docker inspect --format '{{.State.Running}}' "$FAILURE_CONTAINER")" != "false" ]]; then
    echo "Container $FAILURE_CONTAINER was still running: $(docker inspect --format '{{json .State}}' "$FAILURE_CONTAINER")" >&2
    docker logs --tail 20 "$FAILURE_CONTAINER" >&2 || true
  fi
  test "$(docker inspect --format '{{.State.Running}}' "$FAILURE_CONTAINER")" = false
  test "$(docker inspect --format '{{.State.ExitCode}}' "$FAILURE_CONTAINER")" != 0
  docker logs "$FAILURE_CONTAINER" >"$TEST_ROOT/rejected.log" 2>&1
  grep -q 'The Data Protection key repository is unavailable or invalid' "$TEST_ROOT/rejected.log"
  if grep -q 'synthetic-secret-marker' "$TEST_ROOT/rejected.log"; then
    echo "Key material appeared in startup diagnostics" >&2
    return 1
  fi
  if grep -q 'data-protection-root-key' "$TEST_ROOT/rejected.log"; then
    echo "The root-key file name appeared in startup diagnostics" >&2
    return 1
  fi
  docker rm -f "$FAILURE_CONTAINER" >/dev/null
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

bash .github/scripts/test-start-configuration.sh

echo "Checking startup without an explicit host mount"
start_container "$UNMOUNTED_CONTAINER"
check_reported_version "$UNMOUNTED_CONTAINER"
check_runs_unprivileged "$UNMOUNTED_CONTAINER"
check_root_key_file "$UNMOUNTED_CONTAINER"
check_method_route_deleted "$UNMOUNTED_CONTAINER"
check_healthcheck_reports_healthy "$UNMOUNTED_CONTAINER"
docker rm -f -v "$UNMOUNTED_CONTAINER" >/dev/null

echo "Checking explicit Code mode with missing configuration fails safely"
# The docker bridge network (172.16.0.0/12) is trusted for forwarded headers so
# the logout smoke below can speak to the container the way production traffic
# arrives: behind an SSL-terminating proxy. The official antiforgery boundary
# added with B6 (CookieSecurePolicy.Always) rejects a plain-HTTP direct hit.
start_container "$CODE_CONTAINER" --env AdminAuthentication__Provider=OidcCode \
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
docker run --detach --name "$PROVIDER_FAILURE_CONTAINER" \
  --env AdminAuthentication__Provider=Oidc \
  "$IMAGE_NAME" >/dev/null
for _ in $(seq 1 30); do
  if [[ "$(docker inspect --format '{{.State.Running}}' "$PROVIDER_FAILURE_CONTAINER")" != "true" ]]; then
    break
  fi
  sleep 1
done
test "$(docker inspect --format '{{.State.Running}}' "$PROVIDER_FAILURE_CONTAINER")" = false
test "$(docker inspect --format '{{.State.ExitCode}}' "$PROVIDER_FAILURE_CONTAINER")" != 0
docker logs "$PROVIDER_FAILURE_CONTAINER" >"$TEST_ROOT/provider-rejected.log" 2>&1
grep -q 'AdminAuthentication:Provider' "$TEST_ROOT/provider-rejected.log"
grep -q 'HostedLoginReleaseNotes' "$TEST_ROOT/provider-rejected.log"
docker rm -f "$PROVIDER_FAILURE_CONTAINER" >/dev/null

echo "Checking named-volume key storage as the image user"
docker volume create "$NAMED_VOLUME" >/dev/null
start_container "$NAMED_CONTAINER" --volume "$NAMED_VOLUME:/app/data"
check_runs_unprivileged "$NAMED_CONTAINER"
check_root_key_file "$NAMED_CONTAINER"
docker rm -f "$NAMED_CONTAINER" >/dev/null

fresh_data="$TEST_ROOT/fresh"
mkdir -p "$fresh_data"
chmod 700 "$fresh_data"

echo "Checking first-start configuration and database creation"
start_bind_mounted_container "$FRESH_CONTAINER" "$fresh_data"
check_runs_unprivileged "$FRESH_CONTAINER"
check_root_key_file "$FRESH_CONTAINER"
test -s "$fresh_data/appsettings.json"
test "$(docker exec "$FRESH_CONTAINER" stat -c '%a' /app/data/appsettings.json)" = 600
test -s "$fresh_data/vocabulary.db"
check_catalog_is_empty "$FRESH_CONTAINER"
cmp --silent src/Lexarbor.Host/appsettings.json "$fresh_data/appsettings.json"
docker exec "$FRESH_CONTAINER" sh -c 'sha256sum /app/data/data-protection-root-key' >"$TEST_ROOT/original-key-hashes"

echo "Checking new-container request ceilings default to disabled"
jq --exit-status '.RateLimits.AdminLogin.Enabled == false and .RateLimits.PublicApi.Enabled == false' \
  "$fresh_data/appsettings.json" >/dev/null
fresh_port="$(docker port "$FRESH_CONTAINER" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
for path_and_count in 'admin/auth/start 11' 'api/does-not-exist 301'; do
  read -r probe_path probe_count <<<"$path_and_count"
  for _ in $(seq 1 "$probe_count"); do
    probe_status="$(curl --silent --show-error --dump-header "$TEST_ROOT/rate-headers" --output /dev/null --write-out '%{http_code}' \
      "http://127.0.0.1:${fresh_port}/${probe_path}")"
    test "$probe_status" != 429
    ! grep -qi '^Retry-After:' "$TEST_ROOT/rate-headers"
  done
done
docker rm -f "$FRESH_CONTAINER" >/dev/null

echo "Checking specific-audio edit epochs, regeneration and deletion authority survive container recreation"
invalidation_data="$TEST_ROOT/audio-invalidation"
mkdir -p "$invalidation_data"
chmod 700 "$invalidation_data"
cp "$fresh_data/appsettings.json" "$fresh_data/vocabulary.db" "$fresh_data/data-protection-root-key" "$invalidation_data/"
python3 - "$invalidation_data/vocabulary.db" "$TEST_ROOT/invalidation-before.json" <<'PYSQL'
import json
import sqlite3
import sys

with sqlite3.connect(sys.argv[1]) as connection:
    connection.execute("PRAGMA foreign_keys=ON")
    connection.execute("INSERT INTO vocabulary_book (id,book_name,display_order,status,created_at,updated_at) VALUES ('cleanup-book','Cleanup probe',0,1,'2026-01-01','2026-01-01')")
    connection.execute("INSERT INTO vocabulary (id,word,created_at,updated_at) VALUES ('cleanup-word','Cleanup probe','2026-01-01','2026-01-01')")
    connection.execute("INSERT INTO vocabulary_meaning (id,vocabulary_id,book_id,meaning,created_at,updated_at) VALUES ('cleanup-meaning','cleanup-word','cleanup-book','Synthetic cleanup meaning','2026-01-01','2026-01-01')")
    for index, state in enumerate(('Waiting', 'Claimed', 'Observed'), 1):
        asset = f'cleanup-{index}'
        key = f'audio/{index:032x}.mp3'
        connection.execute("INSERT INTO audio_asset VALUES (?,?,?, ?,128,'audio/mpeg',1,2)", (asset, 'test-audio', key, '0' * 64))
        connection.execute("INSERT INTO vocabulary_audio_binding VALUES (?,'cleanup-word','cleanup-meaning','en-GB',?,?)", (asset, asset, '0' * 64))
        connection.execute("INSERT INTO audio_upload_operation (idempotency_key,request_fingerprint,word_id,meaning_id,locale,source_revision,asset_id,state,created_at_unix_ms,updated_at_unix_ms,upload_lease_fence,receipt_status_code,receipt_asset_id,receipt_source_revision,completed_at_unix_ms,cleanup_lease_fence,cleanup_attempts) VALUES (?,?,'cleanup-word','cleanup-meaning','en-GB',?,?,'Succeeded',1,2,1,200,?,?,2,0,0)", (f'00000000-0000-0000-0000-{index:012x}', '0' * 64, '0' * 64, asset, asset, '0' * 64))
        connection.execute("INSERT INTO audio_invalidation (asset_id,bucket,object_key,word_id,meaning_id,locale,authorization_epoch,reason,state,created_at_unix_ms,updated_at_unix_ms,lease_fence,attempts,next_attempt_at_unix_ms) VALUES (?,'test-audio',?,'cleanup-word','cleanup-meaning','en-GB',7,'specific_phonetic_changed','Waiting',3,3,0,0,4)", (asset, key))
        connection.execute("DELETE FROM vocabulary_audio_binding WHERE asset_id=?", (asset,))
        if state != 'Waiting':
            connection.execute("UPDATE audio_invalidation SET state='Claimed',delete_started_at_unix_ms=4,updated_at_unix_ms=4,lease_owner='interrupted',lease_fence=2,lease_expires_at_unix_ms=5,attempts=6 WHERE asset_id=?", (asset,))
        if state == 'Observed':
            connection.execute("UPDATE audio_invalidation SET state='Observed',observed_at_unix_ms=5,updated_at_unix_ms=5,lease_owner=NULL,lease_expires_at_unix_ms=NULL,next_attempt_at_unix_ms=6 WHERE asset_id=?", (asset,))
    connection.execute("UPDATE vocabulary_meaning SET phonetic_uk='edited-specific' WHERE id='cleanup-meaning'")
    connection.execute("INSERT INTO meaning_phonetic_epoch (meaning_id,word_id,locale,epoch) VALUES ('cleanup-meaning','cleanup-word','en-GB',7)")
    connection.execute("UPDATE audio_upload_operation SET meaning_phonetic_epoch=6")
    for step in ('Prepare', 'Audio'):
        target = f'edit-{step}'
        connection.execute("INSERT INTO enrichment_target (id,word_id,meaning_id,locale,step,current_round,epoch,deleted) VALUES (?,'cleanup-word','cleanup-meaning','en-GB',?,1,7,0)", (target, step))
        connection.execute("INSERT INTO enrichment_round (target_id,number,state,input_revision,source_revision,configuration_version,failure_budget,request_count,consumed_failures,lease_fence,created_at_unix_ms,updated_at_unix_ms) VALUES (?,1,'Pending',?,?,'persisted-edit-config',5,0,0,0,3,3)", (target, '0' * 64, '0' * 64))
    snapshot = {table: connection.execute(f"SELECT * FROM {table} ORDER BY 1").fetchall() for table in ('audio_asset', 'audio_upload_operation', 'audio_invalidation', 'meaning_phonetic_epoch', 'enrichment_target', 'enrichment_round')}
    with open(sys.argv[2], 'w', encoding='utf-8') as output:
        json.dump(snapshot, output)
PYSQL
for recreation in 1 2; do
  start_bind_mounted_container "$EXISTING_CONTAINER" "$invalidation_data"
  docker rm -f "$EXISTING_CONTAINER" >/dev/null
  python3 - "$invalidation_data/vocabulary.db" "$TEST_ROOT/invalidation-before.json" <<'PYSQL'
import json
import sqlite3
import sys
with open(sys.argv[2], encoding='utf-8') as source:
    before = json.load(source)
with sqlite3.connect(sys.argv[1]) as connection:
    for table, rows in before.items():
        assert [list(row) for row in connection.execute(f"SELECT * FROM {table} ORDER BY 1")] == rows
    assert connection.execute("PRAGMA foreign_key_check").fetchall() == []
    assert connection.execute("SELECT COUNT(*) FROM vocabulary_audio_binding").fetchone()[0] == 0
    assert connection.execute("SELECT COUNT(*) FROM audio_invalidation WHERE delete_started_at_unix_ms IS NOT NULL").fetchone()[0] == 2
    assert connection.execute("SELECT epoch FROM meaning_phonetic_epoch").fetchone()[0] == 7
    assert connection.execute("SELECT COUNT(*) FROM enrichment_round WHERE state='Pending'").fetchone()[0] == 2
    assert connection.execute("SELECT COUNT(*) FROM audio_upload_operation WHERE meaning_phonetic_epoch=6").fetchone()[0] == 3
PYSQL
done

# Both explicit values must survive upgrade, and higher-priority overrides must
# not modify the operator's file. Tiny quotas make both policies observable.
echo "Checking persisted request switches and environment/argument precedence"
rate_data="$TEST_ROOT/rate-limits"
mkdir -p "$rate_data"
chmod 700 "$rate_data"
for probe_mode in file environment arguments; do
  for login_enabled in true false; do
    if [[ "$login_enabled" == true ]]; then public_enabled=false; else public_enabled=true; fi
    jq --argjson login "$login_enabled" --argjson public "$public_enabled" \
      '.RateLimits.AdminLogin = {Enabled:$login, PermitLimit:1, WindowSeconds:300} | .RateLimits.PublicApi = {Enabled:$public, PermitLimit:1, WindowSeconds:300}' \
      src/Lexarbor.Host/appsettings.json >"$rate_data/appsettings.json"
    cp "$rate_data/appsettings.json" "$TEST_ROOT/rate-before.json"
    probe_env=()
    probe_args=()
    if [[ "$probe_mode" != file ]]; then
      probe_env=(--env "RateLimits__AdminLogin__Enabled=$public_enabled" --env "RateLimits__PublicApi__Enabled=$login_enabled")
    fi
    if [[ "$probe_mode" == environment ]]; then
      expected_login="$public_enabled"
      expected_public="$login_enabled"
    else
      expected_login="$login_enabled"
      expected_public="$public_enabled"
    fi
    if [[ "$probe_mode" == arguments ]]; then
      probe_args=(--RateLimits:AdminLogin:Enabled "$login_enabled" --RateLimits:PublicApi:Enabled "$public_enabled")
    fi
    docker run --detach --name "$EXISTING_CONTAINER" \
      --user "$(id -u):$(id -g)" --volume "$rate_data:/app/data" \
      --publish 127.0.0.1::5008 "${probe_env[@]}" "$IMAGE_NAME" "${probe_args[@]}" >/dev/null
    wait_for_health "$EXISTING_CONTAINER"
    probe_port="$(docker port "$EXISTING_CONTAINER" 5008/tcp | head -n 1 | awk -F: '{print $NF}')"
    for policy in login public; do
      if [[ "$policy" == login ]]; then
        probe_path=admin/auth/start; expected_enabled="$expected_login"
      else
        probe_path=api/does-not-exist; expected_enabled="$expected_public"
      fi
      for attempt in 1 2; do
        probe_status="$(curl --silent --show-error --dump-header "$TEST_ROOT/rate-headers" --output /dev/null --write-out '%{http_code}' \
          "http://127.0.0.1:${probe_port}/${probe_path}")"
        if [[ "$expected_enabled" == true && "$attempt" == 2 ]]; then
          test "$probe_status" = 429
          grep -qi '^Retry-After:' "$TEST_ROOT/rate-headers"
        else
          test "$probe_status" != 429
          ! grep -qi '^Retry-After:' "$TEST_ROOT/rate-headers"
        fi
      done
    done
    cmp --silent "$TEST_ROOT/rate-before.json" "$rate_data/appsettings.json"
    docker rm -f "$EXISTING_CONTAINER" >/dev/null
  done
done

echo "Checking keys survive container recreation with the same data mount"
start_bind_mounted_container "$FRESH_CONTAINER" "$fresh_data"
check_runs_unprivileged "$FRESH_CONTAINER"
check_root_key_file "$FRESH_CONTAINER"
docker exec "$FRESH_CONTAINER" sh -c 'sha256sum /app/data/data-protection-root-key' >"$TEST_ROOT/restarted-key-hashes"
cmp --silent "$TEST_ROOT/original-key-hashes" "$TEST_ROOT/restarted-key-hashes"
check_catalog_is_empty "$FRESH_CONTAINER"
cmp --silent src/Lexarbor.Host/appsettings.json "$fresh_data/appsettings.json"
docker rm -f "$FRESH_CONTAINER" >/dev/null

# Python sqlite3 inspects only schema and synthetic vocabulary rows, never session payloads.
# It runs on the CI/smoke host; the production image stays free of database utilities.
echo "Checking session schema and upgrading a pre-session database without rewriting vocabulary"
upgrade_data="$TEST_ROOT/upgrade"
mkdir -p "$upgrade_data"
chmod 700 "$upgrade_data"
cp "$fresh_data/appsettings.json" "$upgrade_data/appsettings.json"
# Use SQLite backup so the stopped container's retained WAL is included consistently.
python3 - "$fresh_data/vocabulary.db" "$upgrade_data/vocabulary.db" <<'PYSQL'
import sqlite3
import sys
with sqlite3.connect(sys.argv[1]) as source, sqlite3.connect(sys.argv[2]) as target:
    source.backup(target)
PYSQL
python3 - "$upgrade_data/vocabulary.db" "$TEST_ROOT/vocabulary-before.json" <<'PYSQL'
import json
import sqlite3
import sys

with sqlite3.connect(sys.argv[1]) as connection:
    columns = {row[1]: row[2] for row in connection.execute("PRAGMA table_info(admin_session)")}
    assert columns == {"handle_hash": "TEXT", "expires_at_unix_ms": "INTEGER", "protected_payload": "TEXT"}
    assert connection.execute("SELECT name FROM sqlite_master WHERE name LIKE 'textbook_audio_%'").fetchall() == []
    assert connection.execute("PRAGMA foreign_key_check").fetchall() == []
    assert connection.execute("SELECT COUNT(*) FROM admin_session").fetchone()[0] == 0
    assert any(row[1] == "IX_admin_session_expires_at_unix_ms_handle_hash" for row in connection.execute("PRAGMA index_list(admin_session)"))
    # Simulate the retired catalog at the previous migration boundary. Domain
    # migration tests exercise the complete historical schema and upload states.
    connection.execute("DELETE FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_RemoveTextbookAudio'")
    connection.execute("CREATE TABLE textbook_audio_track (id TEXT PRIMARY KEY, book_id TEXT REFERENCES vocabulary_book(id), unit_id TEXT REFERENCES vocabulary_book_unit(id), title TEXT, display_order INTEGER, current_asset_id TEXT REFERENCES audio_asset(id), duration_seconds REAL, is_available INTEGER, version TEXT)")
    connection.execute("CREATE TABLE textbook_audio_upload_operation (idempotency_key TEXT PRIMARY KEY, asset_id TEXT REFERENCES audio_asset(id))")
    connection.execute("CREATE TRIGGER textbook_audio_track_retain_publication BEFORE UPDATE OF first_published_at_unix_ms ON audio_asset WHEN NEW.first_published_at_unix_ms IS NULL AND EXISTS (SELECT 1 FROM textbook_audio_track WHERE current_asset_id=NEW.id AND is_available=1) BEGIN SELECT RAISE(ABORT, 'Retain publication.'); END")
    connection.execute("DROP TABLE admin_session")
    connection.execute("DELETE FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_AddAdminSessions'")
    # A pre-key-repository database also predates the ServiceMantle key table;
    # dropping both the rows and the migration record simulates the real
    # upgrade starting point.
    connection.execute("DROP TABLE service_data_protection_keys")
    connection.execute("DELETE FROM __EFMigrationsHistory WHERE MigrationId LIKE '%_AddServiceDataProtectionKeys'")
    connection.execute("INSERT INTO vocabulary_book (id, book_name, display_order, status, created_at, updated_at) VALUES ('retained-book', 'Retained book', 0, 1, '2026-01-01', '2026-01-01')")
    connection.execute("INSERT INTO vocabulary (id, word, created_at, updated_at) VALUES ('retained-word', 'Retained Word', '2026-01-01', '2026-01-01')")
    connection.execute("INSERT INTO vocabulary_book_unit (id,book_id,number,created_at,updated_at) VALUES ('retained-unit','retained-book',1,'2026-01-01','2026-01-01')")
    connection.execute("INSERT INTO audio_asset (id,bucket,object_key,sha256,byte_length,content_type,created_at_unix_ms,first_published_at_unix_ms) VALUES ('retained-asset','test-audio','audio/retained.mp3',?,128,'audio/mpeg',1,2)", ('0' * 64,))
    connection.execute("INSERT INTO textbook_audio_track (id,book_id,unit_id,title,display_order,current_asset_id,duration_seconds,is_available,version) VALUES ('retained-track','retained-book','retained-unit','Retained track',3,'retained-asset',42,1,'11111111-1111-1111-1111-111111111111')")
    connection.execute("INSERT INTO textbook_audio_upload_operation VALUES ('retired-upload','retained-asset')")
    snapshot = {table: connection.execute(f"SELECT * FROM {table}").fetchall() for table in ("vocabulary_book", "vocabulary", "vocabulary_book_unit", "audio_asset")}
    with open(sys.argv[2], "w", encoding="utf-8") as output:
        json.dump(snapshot, output)
PYSQL
start_bind_mounted_container "$EXISTING_CONTAINER" "$upgrade_data"
check_root_key_file "$EXISTING_CONTAINER"
docker rm -f "$EXISTING_CONTAINER" >/dev/null
python3 - "$upgrade_data/vocabulary.db" "$TEST_ROOT/vocabulary-before.json" <<'PYSQL'
import json
import sqlite3
import sys

with sqlite3.connect(sys.argv[1]) as connection:
    with open(sys.argv[2], encoding="utf-8") as source:
        before = json.load(source)
    for table, rows in before.items():
        assert [list(row) for row in connection.execute(f"SELECT * FROM {table}")] == rows
    assert connection.execute("SELECT name FROM sqlite_master WHERE name LIKE 'textbook_audio_%'").fetchall() == []
    assert connection.execute("PRAGMA foreign_key_check").fetchall() == []
    assert connection.execute("SELECT COUNT(*) FROM admin_session").fetchone()[0] == 0
    assert any(row[1] == "IX_admin_session_expires_at_unix_ms_handle_hash" for row in connection.execute("PRAGMA index_list(admin_session)"))
PYSQL
start_bind_mounted_container "$EXISTING_CONTAINER" "$upgrade_data"
check_root_key_file "$EXISTING_CONTAINER"
docker rm -f "$EXISTING_CONTAINER" >/dev/null

echo "Checking read-only storage cannot serve"
# A read-only data directory must not produce a serving container: startup may
# fail outright (any exit code) or the process may linger, but it never reports
# healthy, and no key material reaches the log either way.
docker run --detach --name "$FAILURE_CONTAINER" \
  --user "$(id -u):$(id -g)" \
  --volume "$fresh_data:/app/data:ro" \
  "$IMAGE_NAME" >/dev/null
served=false
for _ in $(seq 1 30); do
  if [[ "$(docker inspect --format '{{.State.Running}}' "$FAILURE_CONTAINER")" != "true" ]]; then
    served=false
    break
  fi
  if [[ "$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$FAILURE_CONTAINER")" == "healthy" ]]; then
    served=true
    break
  fi
  sleep 1
done
test "$served" = false
docker logs "$FAILURE_CONTAINER" >"$TEST_ROOT/rejected.log" 2>&1
if grep -q 'synthetic-secret-marker' "$TEST_ROOT/rejected.log"; then
  echo "Key material appeared in startup diagnostics" >&2
  exit 1
fi
docker rm -f "$FAILURE_CONTAINER" >/dev/null

echo "Checking a wrong root key fails startup without logging key material"
wrong_key_data="$TEST_ROOT/wrong-key"
mkdir -p "$wrong_key_data"
chmod 700 "$wrong_key_data"
cp "$fresh_data/appsettings.json" "$wrong_key_data/appsettings.json"
cp "$fresh_data/vocabulary.db" "$wrong_key_data/vocabulary.db"
# A fresh random root key does not match the database's existing key rows.
head -c 32 /dev/urandom | base64 >"$wrong_key_data/data-protection-root-key"
chmod 600 "$wrong_key_data/data-protection-root-key"
check_key_startup_rejected "$wrong_key_data" rw "$KEY_REPOSITORY_DIAGNOSTIC"

echo "Checking a loose root-key file fails startup"
loose_key_data="$TEST_ROOT/loose-key"
mkdir -p "$loose_key_data"
chmod 700 "$loose_key_data"
cp "$fresh_data/appsettings.json" "$loose_key_data/appsettings.json"
cp "$fresh_data/vocabulary.db" "$loose_key_data/vocabulary.db"
cp "$fresh_data/data-protection-root-key" "$loose_key_data/data-protection-root-key"
chmod 644 "$loose_key_data/data-protection-root-key"
check_key_startup_rejected "$loose_key_data" rw "$KEY_REPOSITORY_DIAGNOSTIC"

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
printf '%s' 'preserve-existing-database' > "$existing_data/vocabulary.db"

config_hash_before="$(sha256sum "$existing_data/appsettings.json" | cut -d ' ' -f 1)"
database_hash_before="$(sha256sum "$existing_data/vocabulary.db" | cut -d ' ' -f 1)"

echo "Checking that pre-mounted configuration and database files are not overwritten"
# Readiness honestly reports not-ready for the placeholder database, so this
# container never turns healthy by design; verify it serves liveness instead
# and that neither pre-mounted file was rewritten. The key repository probe
# stays silent here: the schema was never verified, and an unverified schema
# is the operator's responsibility, not a key-storage failure.
docker run --detach \
  --name "$EXISTING_CONTAINER" \
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
database_hash_after="$(sha256sum "$existing_data/vocabulary.db" | cut -d ' ' -f 1)"

test "$config_hash_before" = "$config_hash_after"
test "$database_hash_before" = "$database_hash_after"

echo "Container startup and persistence checks passed"
