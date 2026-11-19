# Learner progress API

A trusted backend sends its issued secret in `X-Api-Key` over HTTPS. The key needs the `learning` scope. Lexarbor derives the caller ID from the verified key. The backend authenticates the end user, checks their access to the learner, and supplies the existing student UUID. Teacher actions also carry the authorized actor. Keep keys, caller routing metadata and expected answers inside the backend.

| Method and path under `/learning/v1` | Purpose |
| --- | --- |
| `GET /storage` | Discover the caller's persistent `storeId` |
| `PUT /learners/{uuid}` | Register an existing UUID, idempotently (204) |
| `DELETE /learners/{uuid}` | Delete this caller's learner and all related history, idempotently (204) |
| `POST /plan-owners` | Resolve a JSON array of 1–100 plan UUIDs to known `id`, `studentId` pairs |
| `/api/vocabulary/students/{studentId}/plan`, `/plans` and `/api/vocabulary/plans/...` | Read or set plans, adopt, release, end, batch read and report statistics |
| `/api/vocabulary/students/{studentId}/today`, `/questions/next`, `/reviews`, `/reviews/next` | Today's frozen tasks and free reviews |
| `/api/vocabulary/questions/{questionId}/answer`, `/audio` | Submit one answer or request its pronunciation |
| `/api/vocabulary/students/{studentId}/check-ins`, `/overview`, `/error-prone`, `/words`, `/notebook...` | Read calendars and reports, manage notebook entries, photos and family sources |

For example, a caller can register a learner with `PUT /learning/v1/learners/629f852c-c0f8-46a5-9ac7-355c00ba28f3` and fetch `GET /learning/v1/api/vocabulary/students/629f852c-c0f8-46a5-9ac7-355c00ba28f3/plan`. Vocabulary responses use the existing `success` and `data` envelope. Errors include a message and may include a business error code. For a previously issued choice question, post `studentId`, `optionId`, `elapsedMs`, `localDate` and `tzOffsetMinutes` to its answer route. Retry using the original question ID.

## Migration-aware backends

Bind the discovered store UUID with `X-Learning-Store` and verify the response header of the same name. A store with a published generation requires its UUID in `X-Learning-Generation`. During an operator-controlled rotation a gateway can include the pending UUID in `X-Learning-Pending` while blocking the learner being transferred. A mismatched binding or stale generation returns 409 before executing the operation. Storage discovery is available across generation changes. On delivery errors, retry against the current authoritative store.

## Host configuration

Set `Lexarbor:BaseUrl` to the reachable HTTPS catalog root, `Lexarbor:ApiKey` to a catalog key and `Lexarbor:Integration:ApiKey` to the existing meaning-submissions identity for the notebook integration. The API key issued to the learning backend is separate. Preserve deployment-specific `VocabularyLearning` options, including its `Fsrs` and `ErrorProne` sections, together with the existing catalog, submission configuration and data. If using photo import, preserve the `VocabularyPhotoImport` endpoint, scoped credential, limits and recognition timeout. Back up the complete PostgreSQL database and the deployment's protected key material.
