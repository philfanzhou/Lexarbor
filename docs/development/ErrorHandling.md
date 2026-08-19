# Error handling conventions

## HTTP status code rules

HTTP services must use the standard HTTP status codes:

| Status code | When it is used | Example |
|---------|----------|------|
| `400 Bad Request` | The request, its JSON, or its paging parameters are invalid | A required field is empty, size exceeds 100 |
| `401 Unauthorized` | Login failed, or the JWT is missing or invalid | Anonymous access to an administration endpoint |
| `403 Forbidden` | Authenticated but without the administrator role, or a cookie write request without the same-origin header | An ordinary Identity user reaching an administration endpoint |
| `404 Not Found` | The requested resource does not exist | The word, meaning, or book does not exist |
| `409 Conflict` | A uniqueness, ownership, or deletion conflict | Deleting a book that still has meanings |
| `422 Unprocessable Entity` | A business precondition is not met | The book is disabled, too few question candidates |
| `429 Too Many Requests` | An anonymous endpoint exceeded the ceiling for that client address | Login brute force, one address hammering the public API |
| `500 Internal Server Error` | An internal service error | A database failure, an unexpected error |
| `502 Bad Gateway` | Identity is unreachable or its response is invalid | The administrator login proxy failed |
| `503 Service Unavailable` | Production login configuration is missing | AppId or AppSecret is not configured |

## Error message conventions

1. Error messages are written in English, which keeps internationalization open.
2. An error message is short and specific, and carries no technical detail.
3. The same class of error uses the same wording across services.

## Response envelope format

Every HTTP endpoint returns its response through the `VocabularyHttpResponse` helper:

- success: `{ "success": true, "data": value }` or `{ "success": true }`
- failure: `{ "success": false, "message": "..." }`

## Parameter validation

Parameter validation happens at the HTTP endpoint boundary. A `page` or `size` of 0 falls back to the compatible defaults of 1 and 20; a negative value, a `size>100`, or paging arithmetic that overflows answers 400.

The shared exception middleware maps domain exceptions, database conflicts, JSON errors, and unexpected exceptions onto the status codes above. An endpoint must never return `ex.Message`; a 500 response always uses the generic message.

## Logging conventions

- Use structured log placeholders, never string interpolation.
- The exception object must be passed: `LogError(ex, ...)`, not `LogError(ex.Message, ...)`.
- An expected NotFound is logged at Warning level.
- Never log a password, JWT, cookie, AppSecret, connection string, SQL statement, or a full Identity response.
