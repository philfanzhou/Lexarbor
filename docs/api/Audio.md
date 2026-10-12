# Administrator audio queries

Audio queries use the existing administrator policy: an administrator Bearer token for external programs, or the hosted-login session cookie for the administration UI. A supplied Bearer header takes precedence over a cookie. These GET requests do not require a CSRF header. Keep administrator credentials out of browser application code. Existing security headers, roles and rate-limit policies are unchanged.

This slice provides metadata only. It does not generate audio, upload files, issue playback links, inspect S3, or claim generation jobs. Audio is disabled by default; missing `Audio` configuration is supported. Every successful query includes `enabled`, reflecting `Audio:Enabled`. Metadata remains readable when disabled, but callers must not start audio work when `enabled` is `false`. See [deployment configuration](../development/Deployment.md#optional-audio-object-storage).

## Generation targets

`GET /admin/audio-targets?bookId=...&locale=en-GB&status=missing&page=1&size=20`

All query parameters are optional:

| Parameter | Values and meaning |
| --- | --- |
| `bookId` | Restrict default targets to words with a meaning in this book, and override targets to meanings in this book. Disabled books are readable. Omitting it includes words without any book. An unknown book returns 404. |
| `locale` | Exactly `en-GB` or `en-US`; omit it for both. |
| `status` | Exactly `missing`, `ready`, or `stale`; omit it for all content states. |
| `page` | One-based page number, default 1. |
| `size` | Page size, default 20, from 1 to 100. |

Blank filter values, unsupported locale/status values, nonpositive page/size, oversized pages and offset overflow return 400. The list contains two default targets per word and only the accents of **existing** meaning overrides. It never creates missing override targets for every meaning. Results sort by target type (default before meaning), target ID (word ID or meaning ID, SQLite binary ordering), then locale (`en-GB` before `en-US`). Filtering applies before pagination. `totalCount` and `totalPage` describe the filtered result; an empty result has `totalPage: 0`. A page beyond the last page returns an empty `items` array with the current totals.

Each request reads one consistent SQLite snapshot, including inputs, bindings, states and totals. Different pages are independent snapshots, not a frozen export or a job-claim queue. A generation program should rescan, handle changes and use the target's current version and own binding as upload conditions when the upload API becomes available. Computing content-state filters scans matching targets in bounded batches; page size bounds the response, not the number of inspected targets.

Example response for a word with no audio:

```json
{
  "success": true,
  "data": {
    "enabled": false,
    "items": [
      {
        "wordId": "word-id",
        "meaningId": null,
        "locale": "en-GB",
        "targetType": "default",
        "status": "missing",
        "source": null,
        "sourceRevision": "<64 lowercase hexadecimal characters>",
        "input": {
          "word": "Apple",
          "phonetic": "ˈæpəl",
          "partOfSpeech": null,
          "meaning": null
        },
        "ownBinding": null,
        "effectiveBinding": null
      }
    ],
    "totalCount": 1,
    "totalPage": 1
  }
}
```

This example is a filtered one-target page (for example, with `locale=en-GB`). Default inputs preserve the displayed spelling and include only the requested accent's phonetic. Meaning inputs additionally include part of speech and definition. Examples, textbook enablement, positions and timestamps do not affect the version. Revisions are opaque, deterministic SHA-256 values computed from the versioned spoken input: edits and batch imports both affect them without separate invalidation writes.

## Word and first-override metadata

`GET /admin/vocabulary/{wordId}/audio?meaningId=...`

Omit `meaningId` for defaults; supply an owned meaning ID to inspect an existing or first override. A blank meaning ID returns 400; an unknown word, unknown meaning or a meaning belonging to another word returns 404. Disabled books and words without a book remain readable. Success returns `{ "success": true, "data": { "enabled": false, "items": [...] } }`, with exactly two entries in `en-GB`, `en-US` order and the same target fields as the list.

`sourceRevision` always describes the requested target's **current** generation input. `ownBinding` is the target's own stored binding, or null. `effectiveBinding` is the audio selected for listening, or null. Each binding contains only `assetId` and its stored `sourceRevision`; internal bucket, object key, endpoints, credentials and signed URLs never appear.

For a first meaning override, `ownBinding` is null even when `effectiveBinding` points at the word's default audio. Use the own binding's asset ID as the future upload's `expectedAssetId`; use an empty expected asset when own binding is null. Never substitute the fallback asset ID. The fallback's version is compared with current **default** input, while the returned target version describes the meaning input.

`source` is `default`, `meaning`, or null when missing. A same-accent meaning override wins even when stale. An absent override falls back only to the same accent's default; there is no cross-accent fallback. `missing`, `ready` and `stale` describe the selected binding's content version. They do not prove the remote object exists or is playable, because queries make no S3 calls.

## Errors and cancellation

Success uses the existing `{ "success": true, "data": ... }` envelope. Business failures use `{ "success": false, "message": "<fixed English message>" }`. Missing/invalid administrator identity returns 401, an authenticated non-administrator returns 403, invalid query values return 400, and missing resources or wrong meaning ownership return 404. Framework-level parsing errors retain the host's existing handling. Query cancellation releases the read transaction without creating assets, bindings or upload operations. Existing vocabulary endpoints and JSON contracts are unchanged.
