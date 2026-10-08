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
LEXARBOR_OIDC_CODE_SCOPE='openid profile' \
bash scripts/start.sh >/dev/null
python3 - "$probe_root/arguments" "$probe_root/data/appsettings.json" <<'PY'
import pathlib, sys
arguments = pathlib.Path(sys.argv[1]).read_bytes().decode().split('\0')
expected = {
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
