#!/usr/bin/env bash
# Only disposable local S3 resources and synthetic credentials are used.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"
npm run build --prefix frontend
probe_root="$(mktemp -d)"
container="lexarbor-audio-probe-$(date +%s)-${RANDOM}"
bucket="lexarbor-audio-probe"
proxy_pid=""
cleanup() {
  if [[ -n "$proxy_pid" ]]; then kill "$proxy_pid" 2>/dev/null || true; wait "$proxy_pid" 2>/dev/null || true; fi
  docker rm -f -v "$container" >/dev/null 2>&1 || true
  rm -rf -- "$probe_root"
}
trap cleanup EXIT
cat > "$probe_root/s3.json" <<JSON
{"identities":[
 {"name":"test-setup","credentials":[{"accessKey":"synthetic-setup-access","secretKey":"synthetic-setup-secret"}],"actions":["Admin"]},
 {"name":"audio-only","credentials":[{"accessKey":"synthetic-audio-access","secretKey":"synthetic-audio-secret"}],"actions":["Read:${bucket}","Write:${bucket}"]},
 {"name":"denied","credentials":[{"accessKey":"synthetic-denied-access","secretKey":"synthetic-denied-secret"}],"actions":["Read:other-test-bucket"]}
]}
JSON
docker create --name "$container" --publish 127.0.0.1::8333 \
  chrislusf/seaweedfs:4.00 server -ip=127.0.0.1 -ip.bind=0.0.0.0 -s3 -s3.config=/etc/lexarbor-s3.json >/dev/null
docker cp "$probe_root/s3.json" "$container:/etc/lexarbor-s3.json"
docker start "$container" >/dev/null
port="$(docker port "$container" 8333/tcp | head -n 1 | awk -F: '{print $NF}')"
ready=false
for _ in $(seq 1 60); do
  status="$(curl --silent --max-time 2 --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:$port/" || true)"
  if [[ "$status" = 403 ]]; then ready=true; break; fi
  if [[ "$(docker inspect --format '{{.State.Running}}' "$container")" != true ]]; then break; fi
  sleep 1
done
if [[ "$ready" != true ]]; then echo 'Isolated private S3 did not start.' >&2; exit 1; fi
python3 .github/scripts/audio-storage-proxy.py "$port" "$probe_root/proxy-port" >"$probe_root/proxy.log" 2>&1 &
proxy_pid=$!
for _ in $(seq 1 30); do [[ -s "$probe_root/proxy-port" ]] && break; sleep 1; done
proxy_port="$(cat "$probe_root/proxy-port")"
LEXARBOR_S3_TEST_INTERNAL="http://127.0.0.1:$port/" \
LEXARBOR_S3_TEST_PUBLIC="http://127.0.0.1:$proxy_port/s3/" \
LEXARBOR_S3_TEST_BUCKET="$bucket" LEXARBOR_S3_TEST_DOWNLOAD="$probe_root/download.mp3" \
dotnet run --project tests/AudioStorageProbe --configuration Release
node .github/scripts/test-audio-decoding.mjs "$probe_root/download.mp3"
node .github/scripts/test-audio-decoding.mjs "$probe_root/download.mp3.textbook.mp3"
echo 'Audio storage integration passed.'
