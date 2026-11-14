# Vocabulary family lookup

An API client can read a word's direct inflections and derivations with a
[Lexarbor API key](BusinessApiKeys.md). Send `X-Api-Key` with `catalog` scope. A
missing, invalid or revoked key returns `401`, and a valid key without the scope
returns `403`. Both errors use `{ "success": false, "message": "..." }`.

The endpoints return confirmed relations from the eleven vocabulary relation
types, with current definitions and parts of speech for the related words.
Each link describes a direct, directed word-level relationship. Both endpoints
are read-only and take only catalog lookup criteria.

## Single word

```http
GET /api/vocabulary/happy-id/relations?meaning=%E5%BF%AB%E4%B9%90%E7%9A%84&partOfSpeech=adj.
X-Api-Key: <issued-api-key>
```

```json
{
  "success": true,
  "data": {
    "wordId": "happy-id",
    "items": [
      {
        "relationId": "happy-happiness-id",
        "type": "derived_noun",
        "direction": "outgoing",
        "usageNote": "The state of being happy.",
        "originKind": "generated",
        "word": {
          "wordId": "happiness-id",
          "word": "happiness",
          "normalizedWord": "happiness",
          "phoneticUk": null,
          "phoneticUs": null
        },
        "meanings": [
          {
            "bookId": "supplement-id",
            "bookName": "AI Vocabulary Supplement",
            "meaningId": "happiness-meaning-id",
            "partOfSpeech": "n.",
            "meaning": "幸福",
            "meaningKey": "幸福",
            "example": "She found happiness in her work."
          }
        ]
      }
    ]
  }
}
```

`outgoing` names the stored target as `word`. `incoming` names the stored source.
A self-relation appears once as `outgoing`. Ordering is the catalog type order
listed in [Vocabulary relation management](VocabularyRelations.md), then the
other word's `normalizedWord`, then `relationId`. The result is unpaged.
`meanings` contains every meaning of the other word in enabled books, and is an
empty array when none are available. The `meaningKey` is SQLite's
`lower(trim(meaning))`, as in book entries. `originKind` describes the relation
and can be `manual`, `import`, or `generated`. Confirmed relations from any
of those sources are eligible.

The `meaning` and `partOfSpeech` query parameters are optional. Both use exact
matching after applying the catalog's `lower(trim(...))` rule, which trims
spaces and folds ASCII case. If both are supplied, a single current meaning
in an enabled book must match both. Use the part-of-speech label stored in
the catalog, for example `adj.`. Once the word matches, the response includes
its confirmed word-level links and all enabled-book meanings of each related
word. A blank filter returns
`400`. An unknown word ID or a word with no matching enabled meaning when
filtered returns `404`. A matching word with no confirmed relations returns
`200` with empty `items`.

## Batch lookup

```http
POST /api/vocabulary/relations/lookup
X-Api-Key: <issued-api-key>
Content-Type: application/json

{
  "items": [
    { "word": "settle", "meaning": "定居", "partOfSpeech": "v." },
    { "wordId": "unknown-id" },
    { "word": "settle", "meaning": "定居", "partOfSpeech": "v." }
  ]
}
```

```json
{
  "success": true,
  "data": {
    "items": [
      { "wordId": "settle-id", "found": true, "items": [] },
      { "wordId": "unknown-id", "found": false, "items": [] },
      { "wordId": "settle-id", "found": true, "items": [] }
    ]
  }
}
```

Supply 1–100 lookup entries with either `wordId` or `word` in each entry. Use
`word` for exact spelling by the catalog key, with optional `meaning` and
`partOfSpeech` as in the single-word query. If several catalog rows match the
same spelling and filters, the smallest `wordId` in ordinal order is returned.
Use `wordId` to select a particular catalog row. For ID-only batches, the
shorthand `{"wordIds":["happy-id","settle-id"]}` is also supported.

Results preserve input order and duplicates. A missing match returns
`found: false` with empty `items`. Its `wordId` echoes the requested ID for an ID
lookup and is null for a spelling lookup. A found result contains the resolved
`wordId` and the same ordered `items` as the single-word query. Invalid JSON or
invalid entries return `400`. A body larger than 1 MiB returns `413`.
