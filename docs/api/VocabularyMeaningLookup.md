# Vocabulary meaning lookup

Use `POST /api/vocabulary/meanings/lookup` to read the current meanings for a
batch of complete word or phrase spellings. Send a [Lexarbor API key](BusinessApiKeys.md)
with the `catalog` scope in the `X-Api-Key` header.

```http
POST /api/vocabulary/meanings/lookup
X-Api-Key: <issued-api-key>
Content-Type: application/json

{"words":["  BANK  ","look after","unknown"]}
```

```json
{
  "success": true,
  "data": {
    "items": [
      {
        "word": "  BANK  ",
        "meanings": [
          {
            "wordId": "bank-id",
            "word": "bank",
            "wordKey": "bank",
            "bookId": "core-id",
            "bookName": "Core Vocabulary",
            "meaningId": "bank-noun-id",
            "partOfSpeech": "n.",
            "meaning": "银行",
            "meaningKey": "银行"
          },
          {
            "wordId": "bank-id",
            "word": "bank",
            "wordKey": "bank",
            "bookId": "core-id",
            "bookName": "Core Vocabulary",
            "meaningId": "bank-verb-id",
            "partOfSpeech": "v.",
            "meaning": "把钱存入银行",
            "meaningKey": "把钱存入银行"
          }
        ]
      },
      {
        "word": "look after",
        "meanings": [
          {
            "wordId": "look-after-id",
            "word": "look after",
            "wordKey": "look after",
            "bookId": "supplement-id",
            "bookName": "AI Vocabulary Supplement",
            "meaningId": "look-after-meaning-id",
            "partOfSpeech": null,
            "meaning": "照顾",
            "meaningKey": "照顾"
          }
        ]
      },
      { "word": "unknown", "meanings": [] }
    ]
  }
}
```

`data.items` echoes each input spelling in order, including duplicates.
Spelling matches the whole catalog key. The key trims spaces at both ends and
folds ASCII case, following SQLite's `lower(trim(...))` rule. For example,
`"  BANK  "` matches `bank`, while `banking` requires its own lookup. Spaces
inside a phrase are significant. `meaningKey` follows the same rule for the
meaning text.

Each item contains every matching meaning from enabled books, including enabled
supplementary books. An item with no available meanings has `meanings: []`.
Within `meanings`, each entry carries the stored spelling, definition, book,
identifiers and `partOfSpeech`. The part of speech is the label recorded for that
meaning, or `null` if the label is missing or blank. Identical definition text can
appear in multiple entries, each with its own `meaningId`.

## Limits and errors

Send 1–100 nonblank spellings per request, including duplicates in the count.
The JSON body may be up to 1 MiB. Split larger lists into batches.

| Status | Cause |
| --- | --- |
| `400` | Invalid JSON, a missing or empty `words` array, a null or blank spelling, or too many spellings |
| `401` | Missing, invalid or revoked API key |
| `403` | API key lacks the `catalog` scope |
| `413` | Request body exceeds 1 MiB |

These errors use `{ "success": false, "message": "..." }`. Database failures
use the standard business API problem response.
