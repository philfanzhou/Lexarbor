# Error handling conventions

## HTTP status code rules

HTTP services must use the standard HTTP status codes:

| Status code | When it is used | Example |
|---------|----------|------|
| `400 Bad Request` | The request, its JSON, or its paging parameters are invalid | A required field is empty, size exceeds 100 |
| `401 Unauthorized` | Login failed, or the JWT is missing or invalid | Anonymous access to an administration endpoint |
| `403 Forbidden` | Authenticated but without the administrator role, or a cookie write request without the same-origin header | An ordinary Identity user reaching an administration endpoint |
| `404 Not Found` | The requested resource does not exist | The word, meaning, or book does not exist |
| `413 Payload Too Large` | The request body exceeds the route's size limit | A batch import body over 1 MiB |
| `409 Conflict` | A uniqueness, ownership, or deletion conflict | Deleting a book that still has meanings |
| `422 Unprocessable Entity` | A business precondition is not met | The book is disabled, too few question candidates |
| `429 Too Many Requests` | An anonymous endpoint exceeded the ceiling for that client address | Login brute force, one address hammering the public API |
| `500 Internal Server Error` | An internal service error | A database failure, an unexpected error |
| `502 Bad Gateway` | Identity is unreachable or its response is invalid | The administrator login proxy failed |
| `503 Service Unavailable` | Production login configuration is missing, or the database is busy | AppId or AppSecret is not configured; a contended SQLite write |

## Error message conventions

1. Error messages are written in English, which keeps internationalization open.
2. An error message is short and specific, and carries no technical detail.
3. The same class of error uses the same wording across services.

## Two failure shapes

Failures reach a caller in one of two shapes, depending on where they originate.

**Endpoint-explicit failures** keep the legacy envelope, written through the
`VocabularyHttpResponse` helper:

- success: `{ "success": true, "data": value }` or `{ "success": true }`
- failure: `{ "success": false, "message": "..." }`

`POST /admin/vocabulary/batch` adds one field to its 400 answer for invalid entries: `errors`, a list of `{ "index": n, "message": "..." }` naming every invalid entry by its zero-based position. No other route returns `errors`. See [ADR-005](../adr/ADR-005-bulk-vocabulary-import.md).

**Exception-generated failures** — an exception that escapes an endpoint and
has not started writing the response — are answered by the ServiceMantle
Problem Details pipeline as `application/problem+json`:

```json
{
  "type": "urn:servicemantle:error:vocabulary.conflict",
  "title": "The request conflicts with existing data.",
  "status": 409,
  "correlationId": "3609a6f2651043efaecca95e276f7319",
  "errorCode": "vocabulary.conflict"
}
```

Every response also carries the correlation id as an `x-correlation-id` header.
The mappings are fixed per exception type and are registered in the Host:

| Exception | Status | `errorCode` | `title` |
|---|---|---|---|
| `DomainValidationException` | 400 | `vocabulary.validation` | The request is invalid. |
| `BadHttpRequestException` (non-413) | 400 | `vocabulary.bad_request` | The request is invalid. |
| `BadHttpRequestException` (Kestrel body limit) | 413 | `vocabulary.request_too_large` | The request body is too large. |
| `ResourceNotFoundException` | 404 | `vocabulary.not_found` | The requested resource was not found. |
| `ConflictException` | 409 | `vocabulary.conflict` | The request conflicts with existing data. |
| `BusinessRuleException` | 422 | `vocabulary.business_rule` | The request violates a business rule. |
| `StorageBusyException` | 503 | `vocabulary.storage_busy` | The vocabulary database is temporarily busy. |
| anything else | 500 | `http.internal_server_error` | An unexpected error occurred. |

`StorageBusyException` answers with `Retry-After: 1`; no other mapping sets
response headers beyond the correlation id.

Clients should read the failure by content: parse the Problem Details fields
when `title` is present, the envelope `message` otherwise. The administration
frontend does this in `frontend/src/services/apiError.ts`; both shapes will
remain in use for the foreseeable future because endpoint-explicit rejections
are not being converted.

## Parameter validation

Parameter validation happens at the HTTP endpoint boundary. A `page` or `size` of 0 falls back to the compatible defaults of 1 and 20; a negative value, a `size>100`, or paging arithmetic that overflows answers 400.

The ServiceMantle exception pipeline maps domain exceptions, database conflicts, JSON errors, and unexpected exceptions onto the status codes above. Kestrel reports a body over the route's size limit with its own derived `BadHttpRequestException`, which is mapped separately from the public base class so both hosts answer 413 `The request body is too large.` with the usual problem shape; that reaches the pipeline only on a route that reads its own body, such as the batch import, because a route that binds its body as a parameter has an oversized body answered by the framework with an empty 413. An exception's `Message` never reaches a response; titles are fixed at registration, and a 500 always uses the generic title.

## Logging conventions

- Use structured log placeholders, never string interpolation.
- The exception object must be passed: `LogError(ex, ...)`, not `LogError(ex.Message, ...)`.
- The ServiceMantle Problem Details middleware logs every mapped failure at Error level with its error code and correlation id; no exception text, SQL, or credential is logged.
- Never log a password, JWT, cookie, AppSecret, connection string, SQL statement, or a full Identity response.

## Breaking-change history

- Exception-generated failure responses changed from the envelope
  `{ "success": false, "message": "..." }` with `application/json` to the
  ServiceMantle Problem Details document above with
  `application/problem+json`. Status codes, the busy-database `Retry-After: 1`,
  and all endpoint-explicit envelopes (including the batch import's per-entry
  `errors`) are unchanged. Clients that parsed only `message` need to read
  `title` for these responses.
