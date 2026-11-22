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

## Review locations

A memory card is reviewed at the catalog location of the new or extra question that created it. Later questions do not change this location, including word-family questions and questions that expired unanswered. The card also records where the word came from: the plan, the notebook, or a notebook item added only through a word family. A notebook word learned at the plan's location still counts as a notebook word. Cards created before learner storage version 6 have no recorded source.

When the saved location cannot produce a question, for example because its book is disabled or the meaning was removed, Lexarbor uses the location of the card's latest answered question for that review only. The saved location stays unchanged. If neither location produces a question, a daily review drops the card from that day's review group and a free review skips it, as before. Include the `vocab_card_location` table when backing up or transferring learner schemas. Without it, reviews fall back to the latest answered location.

## Unit practice

Use `/learning/v1/api/vocabulary/students/{studentId}/unit-practice` as the base path. Register the learner first. A learner can practice a unit without an active daily plan. Each learner has one current session across devices. Starting a replacement removes the previous session and its answers.

Populate the book picker with `GET /api/vocabulary-books/unit-practice` using a catalog-scoped key. It returns enabled books in display order under `data.books` and excludes the book Lexarbor created for automatic vocabulary expansion. A manually imported book remains eligible even if an administrator configures it as the expansion destination. Use `GET /api/vocabulary-books/{bookId}/units` to load the selected book’s units.

| Method and relative path | Request | Result |
| --- | --- | --- |
| `GET` at the base path | No body | The current session, or `data: null` |
| `POST` at the base path | `sessionId` (a fresh client-generated UUID), `bookId`, `unitId`, optional `mode` and `previousSessionId` | Starts round 1. Repeat with the same UUID, scope and mode to resume. To replace a session, supply its UUID as `previousSessionId`. |
| `POST /{sessionId}/answers` | `questionId` and exactly one of `answer` (the full English spelling), `optionId`, `letters` or `unknown: true`, matching the question type | Returns `questionId`, `correct`, the target word and meaning, and `correctOptionId` for a choice. The first submission for a question wins. |
| `PUT /{sessionId}/rounds/{round}` | No body | Starts the next round of outstanding mistakes. Round numbers start at 2 for the first retry. Repeating the current retry round resumes it. |

The start request snapshots distinct word and meaning pairs from the selected unit, including previously learned words. A catalog error or empty unit leaves the current session intact. Reading and answering an existing session work during catalog outages. Practice answers have their own question IDs and never enter daily tasks, check-ins, FSRS scheduling or review logs.

For example, a start request can send:

```json
{"sessionId":"eeeed4a4-706e-44df-944b-370c6a50e909","bookId":"book-1","unitId":"unit-3"}
```

### Practice modes

`mode` is `spelling` (the default when omitted), `choice` or `mixed`. Every response reports the session's `mode`. Retry rounds keep it. Starting with a different mode requires a replacement session.

| Mode | Questions |
| --- | --- |
| `spelling` | The learner reads the Chinese meaning and writes the full English spelling. |
| `choice` | A [`ChineseToEnglish`](LearningQuestions.md#selection-and-response-contract) choice for every target. |
| `mixed` | The catalog `review` draw for every target: `EnglishToChinese`, `ChineseToEnglish`, `FillLetters` or `PartOfSpeechChoice`. Unit practice sends no `relation`, so the three related types do not occur. |

Choice and mixed sessions generate the whole first pass when the session starts. Lexarbor first draws distractors from the selected unit, then from the whole book. A target that still has no question is asked as a spelling question. A catalog outage while generating returns 503 and leaves the current session intact. Each retry round asks the catalog for new questions, so a mixed retry can use a different type than the first pass. If the catalog is unavailable or cannot serve a target, the retry reissues the first-pass question with new option IDs in a new order.

`nextQuestion.type` is `Spelling` or one of those question types. Each question also has `questionId` and `position`. A spelling question has `meaning`, `partOfSpeech` and `spellingHint`, as before modes existed. Other types have `prompt` and follow the [learning-question shapes](LearningQuestions.md#selection-and-response-contract). Choices have four `options` with `optionId` and `text`. `FillLetters` has `pattern`. `PartOfSpeechChoice` also has `anchorWord` and the `partOfSpeech` code. Correct flags and answer words are never sent. Answer a spelling question with `answer`, a choice with one of its `optionId` values and `FillLetters` with `letters`, which holds one letter for each null in `pattern`. `unknown: true` answers any type. A mismatched or malformed answer returns 400.

For example, a choice session starts with:

```json
{"sessionId":"4e0f1f4c-5b0f-4c55-9a43-1f0d6b8c2e10","bookId":"book-1","unitId":"unit-3","mode":"choice"}
```

### Spelling questions

Display the meaning and optional part of speech from `nextQuestion` and ask for the English spelling. When multiple targets have the same prompt, display the optional `spellingHint`, which shows the word length and distinguishes competing spellings. A bracket such as `[≠R/P]` marks one position where the letter differs from both R and P. This keeps at least one letter hidden even when every position is needed to distinguish the target. Submit its opaque `questionId` with the answer. Grading ignores surrounding whitespace and letter case.

### Rounds and progress

Fetch the current session after answering to obtain the next prompt. `total`, `answered` and `firstPassCorrect` refer to distinct meaning targets in the first pass. `roundTotal` and `roundAnswered` describe the current round. A null `nextQuestion` means that round is complete. The end-of-round `mistakes` list contains first-pass misses and marks those corrected during retries. A new retry round gets fresh question IDs for the remaining misses. First-pass question IDs stay valid for the lifetime of the session. A new retry for a target replaces its previous retry question ID.

To add a missed meaning to the notebook, send its returned `target.word` and `target.meaningId` to the existing `POST /learning/v1/api/vocabulary/students/{studentId}/notebook/items` route with an authorized actor after the student selects it.

An invalid body or unknown mode returns 400. An unknown learner, session or question returns 404. A stale replacement or invalid round transition returns 409. An empty unit or disabled book returns 422. A catalog outage during session creation returns 503.

## Migration-aware backends

Bind the discovered store UUID with `X-Learning-Store` and verify the response header of the same name. A store with a published generation requires its UUID in `X-Learning-Generation`. During an operator-controlled rotation a gateway can include the pending UUID in `X-Learning-Pending` while blocking the learner being transferred. A mismatched binding or stale generation returns 409 before executing the operation. Storage discovery is available across generation changes. On delivery errors, retry against the current authoritative store.

## Host configuration

Set `Lexarbor:BaseUrl` to the reachable HTTPS catalog root, `Lexarbor:ApiKey` to a catalog key and `Lexarbor:Integration:ApiKey` to the existing meaning-submissions identity for the notebook integration. The API key issued to the learning backend is separate. Preserve deployment-specific `VocabularyLearning` options, including its `Fsrs` and `ErrorProne` sections, together with the existing catalog, submission configuration and data. If using photo import, preserve the `VocabularyPhotoImport` endpoint, scoped credential, limits and recognition timeout. Back up the complete PostgreSQL database and the deployment's protected key material.

### Learning days

Daily tasks, check-ins, plan statistics, the overview and plan start dates split time into learning days on the server. Set `VocabularyLearning:TimeZone` (for example `VocabularyLearning__TimeZone=Asia/Shanghai`) to a time zone ID that the host knows, such as `Asia/Shanghai`, `Asia/Tokyo` or `UTC`. When it is unset, a learning day starts at midnight UTC+08:00 with no daylight saving time, as in earlier releases. An unknown ID stops startup with an error naming the setting. The setting applies to every caller of the deployment.

The `localDate` and `tzOffsetMinutes` values that clients send are still required and range-checked, but they do not change the learning day. The overview reports the server's `localDate` and the zone's current `tzOffsetMinutes`, which can change with daylight saving time.

Changing the time zone moves the boundary between learning days. The day in progress can end early or run longer, and dates already stored, such as completed days and plan start dates, keep their values. Change it between learning days and tell learners about the shift.
