# System settings

The administrator page at `/#/settings` displays effective request ceilings, their
configuration source, and separate unsaved switch drafts. A failed read means
**unknown**, never disabled. Numeric limits and windows are read-only; change them
offline and restart. Audio storage settings are not exposed by this page yet.

## Request rate limits

`GET /admin/system/settings/rate-limits` returns the following JSON directly
(without a vocabulary response envelope):

```json
{
  "revision": "\"0123456789abcdef0123456789abcdef\"",
  "adminLogin": {
    "enabled": false,
    "permitLimit": 10,
    "windowSeconds": 300,
    "source": "default",
    "editable": true
  },
  "publicApi": {
    "enabled": false,
    "permitLimit": 300,
    "windowSeconds": 60,
    "source": "default",
    "editable": true
  }
}
```

`ETag` equals the opaque `revision`, including its quotes. `source` describes the
switch: `default`, `file`, `environment`, or `commandLine`. The latter two sources
have `editable: false`; the other switch may still be saved independently. The
numeric values are the effective startup configuration, including invalid values
when disabled. A disabled policy with invalid numbers cannot be enabled online.

`PUT /admin/system/settings/rate-limits` accepts at least one boolean field:

```json
{ "adminLoginEnabled": true, "publicApiEnabled": false }
```

Omitted switches retain their effective values. Unknown or duplicate fields,
non-booleans, and an empty object are rejected. Supply the current **strong**
`If-Match` value exactly as returned by GET. A successful PUT returns the same
shape as GET and an ETag for that response snapshot. All setting reads and writes
require the existing administrator authorization; anonymous callers receive 401
and non-administrators 403. Session-cookie PUTs require `X-SignaCore-CSRF` and its
antiforgery cookie. An explicit Bearer header takes precedence over a cookie;
an invalid Bearer token cannot fall back to a valid session. Setting responses,
including errors, are `Cache-Control: no-store` and carry the administrator
security headers. Settings APIs do not consume a rate-limit budget.

| Status | Result |
|---|---|
| 200 | File committed and runtime switches published; GET confirms current state |
| 400 | Invalid JSON/fields, malformed or weak If-Match, or invalid enabled quota/window |
| 409 | Requested switch is overridden by environment/command-line configuration |
| 412 | Stale revision, concurrent save, or offline disk edit; refresh before retrying |
| 413 | Payload exceeds 4096 bytes |
| 428 | Missing If-Match |
| 503 | Configuration cannot be safely written; old runtime state remains active |

Failures use fixed English `message` text, with safe field names on an override
conflict. They never echo input. Versions are shared across all setting modules;
a save in another module invalidates an older settings page.

## Persistence and runtime behavior

Both ceilings default to disabled. Enabling uses the existing ServiceMantle
named policy and per-client-address sliding-window bucket. Publishing a change
applies to requests that enter the limit pipeline afterwards; an in-flight
request keeps its decision. Disabling skips the policy, consumes no permits,
and does not add that policy's Retry-After. Re-enabling retains a still-live
bucket and does not reset consumed permits. Login and public API budgets remain
independent. Only `/admin/auth/start` and `/api/*` are metered; unknown public
routes, browsing, question batches, and audio playback share the public budget.
Callback, logout, health, SPA, administrator business, and settings routes remain
unmetered. Audio upload concurrency/queue admission is a separate unchanged guard.

A save modifies only allowed switches in `<contentRoot>/data/appsettings.json`
(`/app/data/appsettings.json` in containers), preserving unrelated JSON fields.
The shared coordinator creates missing directories/files, writes a same-directory
temporary file, flushes it, atomically replaces the target, then publishes one
immutable runtime snapshot. The source template is never overwritten. Unix files
are created as 0600 and new data directories as 0700. Read-only files/directories,
unsafe links, malformed JSON, and write failures keep the old file and runtime;
GET does not create the file. Restart loads the saved switches with the existing
precedence: defaults < persisted file < environment < command line. Offline
edits require restart; online file editing concurrent with page saves is unsupported.

The page disables duplicate submission, keeps nonsecret drafts after failures,
and reads back after saving. A lost PUT response may mean the file was committed:
it displays an unknown outcome and performs GET, never automatically resubmits.
If confirmation fails, retry the read and compare the current state with the draft.
Leaving the page or logging out aborts reads and discards drafts; no draft is
stored in browser storage or a URL. The exact `/settings` return target is accepted
by hosted login; external URLs and encoded variants remain rejected.

These are process-local request ceilings, not distributed quotas or DDoS protection.
Configure trusted proxies correctly and respect 429 Retry-After guidance; see
[Deployment](../development/Deployment.md#rate-limits-and-client-addresses).
