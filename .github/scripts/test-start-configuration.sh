#!/usr/bin/env bash
# Run the real startup script against a Docker command recorder. No credentials
# are printed, no Docker resources are created, and all values are synthetic.
set -euo pipefail
probe_root="$(mktemp -d)"
trap 'rm -rf -- "$probe_root"' EXIT
mkdir -p "$probe_root/bin" "$probe_root/data"
cat > "$probe_root/bin/docker" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1" = run ]]; then
  printf '%s\0' "$@" > "$LEXARBOR_MAPPING_PROBE"
fi
STUB
chmod +x "$probe_root/bin/docker"
printf '%s' '{"existing":"unchanged"}' > "$probe_root/data/appsettings.json"
PATH="$probe_root/bin:$PATH" LEXARBOR_MAPPING_PROBE="$probe_root/arguments" \
LEXARBOR_DATA_DIR="$probe_root/data" LEXARBOR_ADMIN_AUTH_PROVIDER=OidcCode \
LEXARBOR_OIDC_CODE_CLIENT_ID=synthetic-client \
LEXARBOR_OIDC_CODE_CLIENT_SECRET=synthetic-code-secret \
LEXARBOR_OIDC_CODE_REDIRECT_URI='https://lexarbor.test/admin/auth/callback' \
LEXARBOR_OIDC_CODE_POST_LOGOUT_REDIRECT_URI='https://lexarbor.test/admin/auth/logout/return' \
LEXARBOR_AUDIO_ENABLED=true LEXARBOR_AUDIO_INTERNAL_ENDPOINT='http://s3.test:8333/' \
LEXARBOR_AUDIO_PUBLIC_ENDPOINT='https://audio.test/s3/' LEXARBOR_AUDIO_ACCESS_KEY=synthetic-audio-access \
LEXARBOR_AUDIO_SECRET_KEY=synthetic-audio-secret LEXARBOR_AUDIO_REGION=us-east-1 \
LEXARBOR_AUDIO_BUCKET=lexarbor-audio LEXARBOR_AUDIO_KEY_PREFIX=audio/ \
LEXARBOR_AUDIO_PLAYBACK_TTL_SECONDS=60 LEXARBOR_AUDIO_MAX_UPLOAD_BYTES=1048576 \
LEXARBOR_AUDIO_MAX_CONCURRENT_UPLOADS=1 LEXARBOR_AUDIO_MAX_QUEUED_UPLOADS=0 \
LEXARBOR_AUDIO_OPERATION_TIMEOUT_SECONDS=3 \
LEXARBOR_OIDC_CODE_SCOPE='openid profile' \
bash scripts/start.sh >/dev/null
python3 - "$probe_root/arguments" "$probe_root/data/appsettings.json" <<'PY'
import pathlib, sys
arguments = pathlib.Path(sys.argv[1]).read_bytes().decode().split('\0')
expected = {
    'Audio__Enabled': 'true',
    'Audio__InternalEndpoint': 'http://s3.test:8333/',
    'Audio__PublicEndpoint': 'https://audio.test/s3/',
    'Audio__AccessKey': 'synthetic-audio-access',
    'Audio__SecretKey': 'synthetic-audio-secret',
    'Audio__Region': 'us-east-1',
    'Audio__Bucket': 'lexarbor-audio',
    'Audio__KeyPrefix': 'audio/',
    'Audio__PlaybackTtlSeconds': '60',
    'Audio__MaxUploadBytes': '1048576',
    'Audio__MaxConcurrentUploads': '1',
    'Audio__MaxQueuedUploads': '0',
    'Audio__OperationTimeoutSeconds': '3',
    'AdminAuthentication__Provider': 'OidcCode',
    'AdminAuthentication__OidcCode__ClientId': 'synthetic-client',
    'AdminAuthentication__OidcCode__ClientSecret': 'synthetic-code-secret',
    'AdminAuthentication__OidcCode__RedirectUri': 'https://lexarbor.test/admin/auth/callback',
    'AdminAuthentication__OidcCode__PostLogoutRedirectUri': 'https://lexarbor.test/admin/auth/logout/return',
    'AdminAuthentication__OidcCode__Scope': 'openid profile',
}
for key, value in expected.items():
    assert arguments.count(key + '=' + value) == 1, 'Code configuration mapping failed'
# The removed password-proxy settings, and the removed Testing-only plain-HTTP
# hosted-login transport, must not be mapped at all anymore.
for removed in (
    'AdminAuthentication__CookieSecure=',
    'AdminAuthentication__Oidc__',
    'AdminAuthentication__Gateway__',
    'AdminAuthentication__HttpTestOrigins=',
):
    assert not any(argument.startswith(removed) for argument in arguments), \
        'Removed authentication mapping still present: ' + removed
assert pathlib.Path(sys.argv[2]).read_text() == '{"existing":"unchanged"}'
PY
printf '%s\n' 'Startup Code configuration mapping passed'
