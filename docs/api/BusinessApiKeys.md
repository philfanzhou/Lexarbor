# Business API keys

Lexarbor issues API keys for server-to-server calls. Send one key in the
`X-Api-Key` header over HTTPS. Keep keys in the calling service's backend secret
store. For playback in a browser, the backend obtains the short-lived signed
audio URL and passes the URL to the browser.

| Scope | Routes |
| --- | --- |
| `catalog` | Vocabulary and book queries, single, batch and learning questions, and audio link issuance under `/api/vocabulary*` |
| `meaning-submissions` | `/integration/vocabulary/search` and `/integration/meaning-submissions` with its lookup and question subroutes |

A key can carry either or both scopes. Missing, malformed, unknown and revoked
keys return `401` with `WWW-Authenticate: ApiKey`. A valid key missing the
required scope returns `403`. Both responses use `{ "success": false, "message": "..." }`.
Authentication and authorization precede business input validation and queries.
An administrator session or OIDC Bearer token grants access to management routes.
Business API keys grant access only to the assigned business scopes.

## Issue, list and revoke

In the administration UI, open the [API key settings](../frontend/README.md#routes). Select the scopes and
enter a stable, case-sensitive caller ID. The ID can contain 1–2048 characters
without whitespace or control characters. Save the key when it is displayed.

The management routes also support the configured administrator Bearer token or
an administrator session. For a session, writes require the existing
`X-SignaCore-CSRF` header and antiforgery cookie.

```http
POST /admin/api-keys
Authorization: Bearer <administrator-token>
Content-Type: application/json

{"callerId":"study-backend","scopes":["catalog"]}
```

```json
{
  "success": true,
  "data": {
    "key": "<issued-api-key>",
    "metadata": {
      "id": "31a20d15-d80d-4ab4-b67e-a8590fe90c4b",
      "callerId": "study-backend",
      "scopes": ["catalog"],
      "createdAtUnixMs": 1794628800000,
      "revokedAtUnixMs": null
    }
  }
}
```

Issuance returns the secret once with `Cache-Control: no-store`. `GET
/admin/api-keys` returns `{"success":true,"data":{"items":[...]}}` with metadata
only, including revoked keys. `DELETE /admin/api-keys/{id}` uses the metadata ID
and returns `{"success":true}` for both first and repeated revocations. An
unknown ID returns `404`. A revoked key stops authenticating new requests after
commit. Requests that already passed authorization can complete.

Issue the replacement with the same caller ID and needed scopes, switch the
backend to the replacement, then revoke the old key. If an issuance response is
lost, list the keys to identify and revoke the unused key before retrying. Keys
have no time-based expiry. Both issuance and revocation survive restarts.

The submission routes share one review and lookup workflow. Lexarbor permits
active keys with `meaning-submissions` for one caller ID at a time. Attempts to
grant it to another caller while such a key is active return `409`. Rotation can
overlap keys for the same caller. When migrating the former JWT caller, enter
its exact previous `MeaningSubmissions:Integration:Subject` as the caller ID so
new submissions retain the existing source identity. Preserve existing submission
IDs in the calling service.

## Call a business route

```http
GET /api/vocabulary-books/all
X-Api-Key: <issued-api-key>
```

```json
{"success":true,"data":{"books":[]}}
```

See [the deployment upgrade steps](../development/Deployment.md#business-api-key-upgrade).
