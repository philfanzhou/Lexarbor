# Meaning submissions API

Integration clients submit definitions that no vocabulary book contains. Administrators can review submissions in the administration UI; explicitly enabling automatic AI review lets the background worker decide. Approval writes the reviewed definition into the single supplementary book the deployment configures. See [ADR-009](../adr/ADR-009-meaning-submissions.md) for the decision record.

All responses use the usual envelope: `{"success": true, "data": ...}` or `{"success": false, "message": "..."}`. Exception-generated failures use the Problem Details mapping in [Error handling](../development/ErrorHandling.md).

## Submission object

| Field | Description |
|---|---|
| `id` | Submission ID |
| `word`, `partOfSpeech`, `meaning`, `example` | What the client submitted (trimmed; the part of speech is lower-cased) |
| `status` | `pending`, `approved` or `rejected` |
| `resultBookId`, `resultWordId`, `resultMeaningId` | Where the approved definition is stored; set only when approved |
| `resultWord`, `resultPartOfSpeech`, `resultMeaning` | The approved text, possibly corrected by the administrator |
| `definitionKept` | `true` when approved with the submitted spelling and definition, compared by their catalog keys |
| `wordKey`, `meaningKey` | Catalog keys of the submitted text: `lower(trim(...))` with ASCII-only folding, the same rule as `normalizedWord` and `meaningKey` in `GET /api/vocabulary-books/{bookId}/entries` |
| `resultWordKey`, `resultMeaningKey` | Catalog keys of the approved text; set only when approved |
| `createdAt`, `reviewedAt` | UTC timestamps |

## Integration routes

These routes require an integration service token (see [Authentication](#integration-authentication)). They answer `503` when `MeaningSubmissions:BookId` is not configured.

### `POST /integration/meaning-submissions`

Body: `{"word": "serendipity", "partOfSpeech": "n.", "meaning": "意外发现", "example": null}`. `word` (≤100 characters) and `meaning` (≤500) are required; `partOfSpeech` ≤32, `example` ≤1000.

Answers the submission. An equivalent submission — same normalized word and part of speech and the same trimmed definition — that is pending, or approved with `definitionKept`, is answered instead of a new one. When the supplementary book already holds the equivalent definition, the answer is an approved submission pointing at it. A rejected definition can be submitted again and becomes a new pending submission.

### `GET /integration/vocabulary/search?keyword=...`

Lets the client offer existing meanings before it submits. `keyword` (1–100 characters) is compared by its catalog key; the answer is `{"items": [...]}` with every meaning in an enabled book (the supplementary book included) of up to 10 words spelled exactly like the keyword or starting with it — the exact spelling first, then shorter words. Each item has `wordId`, `word`, `wordKey`, `phoneticUk`, `phoneticUs`, `bookId`, `bookName`, `meaningId`, `partOfSpeech`, `meaning`, `meaningKey` and read-only `pronunciation`.

The old phonetic fields remain word defaults. `pronunciation.uk` and `.us` each
contain `{phonetic, source}` with the effective phonetic (null when empty) and
`default`/`meaning` inheritance source, following the same
[projection as public detail and entries](Audio.md#read-only-pronunciation-metadata).
Pass this item's `meaningId` and `bookId` to public playback to hear that meaning;
an explicit differing pronunciation with no own audio returns `missing`.
Submission and approval bodies do not accept this projection as a phonetic edit.

### `POST /integration/meaning-submissions/lookup`

Body: `{"ids": ["..."]}` with 1–100 IDs. Answers `{"items": [submission, ...]}`; unknown IDs are omitted.

### `POST /integration/meaning-submissions/{id}/question`

Body: `{"chineseToEnglish": true}` (omitted: random). Answers the same shape as `POST /api/vocabulary/question`.

- Pending: the stem and correct option are the submitted text; distractors come from every enabled book.
- Approved with `definitionKept`: the question is created from the approved meaning in the supplementary book.
- Rejected, or approved with a changed spelling or definition: `404`.
- Too few distinct catalog words for four options: `422`.

## Administrator routes

These routes require the administrator role, like every `/admin` route.

| Route | Description |
|---|---|
| `GET /admin/meaning-submissions?status=pending&page=1&size=20` | One page of submissions (`pending` oldest first; `approved`/`rejected` newest review first). The data also carries `configured`, `bookId` and `assistantEnabled`; each item is `{"submission": ..., "source": "<client subject>"}` |
| `POST /admin/meaning-submissions/{id}/approve` | Body `{"word", "partOfSpeech", "meaning", "example", "phoneticUk", "phoneticUs"}`; writes into the supplementary book with the import matching rules. `409` when the submission was already reviewed |
| `POST /admin/meaning-submissions/{id}/reject` | Marks a pending submission rejected; `409` when already reviewed |
| `POST /admin/meaning-submissions/{id}/advice` | Asks the optional review assistant; answers `{"verdict": "accept"|"revise"|"reject", "partOfSpeech", "meaning", "phoneticUk", "phoneticUs", "comment"}`. `422` when the assistant is not configured, `502` when it gives no usable answer |

## Supplementary-book questions

`POST /api/vocabulary/question` for the supplementary book tops up missing distractors from every enabled book, because the book only holds definitions no other book had. Every other book still draws distractors only from itself.

## Integration authentication

The client signs a short-lived RS256 JWT per request with its deployment private key and sends it as `Authorization: Bearer <token>`. Lexarbor accepts it only when:

- the header `kid` names a key in the configured public-key ring and `alg` is `RS256`;
- `iss`, `aud` and `sub` equal the configured values;
- `iat`, `nbf` and `exp` each appear once, `exp - iat` is 1–60 seconds, `iat` is not more than 5 seconds in the future, and lifetime validation allows 5 seconds of skew;
- the space-separated `scope` claim contains `meaning-submissions`.

A missing or invalid token answers `401`; a valid token without the scope answers `403`. Administrator sessions and administrator Bearer tokens never authenticate integration routes, and integration tokens never authenticate administrator routes. Configuration is described in [Deployment](../development/Deployment.md#meaning-submissions).

## Enrichment after approval

Approved catalog changes follow the ordinary durable enrichment discovery path
when its independent switches and required configuration are ready. Approval
commits before any AI/TTS work; it does not promise immediate generation or enable
automatic AI review. Empty specific phonetics inherit the default. A later actual
specific edit uses the shared atomic detach/cleanup/regeneration boundary. See
[enrichment acceptance](../development/EnrichmentAcceptance.md) for automated
coverage and the separate pending supplier-listening checks.

## Review outcome metadata

Submission, lookup and administrator submission objects add nullable
`reviewComment`. It is trimmed (maximum 500 characters), empty becomes `null`,
and it is exposed only for rejection or approval with `definitionKept=false`.
Ordinary approval and AI accept always return `null`. Existing fields and routes
are unchanged. The administrator list wrapper additionally returns nullable
`reviewerKind`: `admin`, `ai`, or `null` for pending, legacy and direct catalog
matches. `source` remains the submitting integration subject.

`POST /admin/meaning-submissions/{id}/approve` accepts optional `reviewComment`
with the existing approval fields. `POST /admin/meaning-submissions/{id}/reject`
continues accepting no body, or accepts `{ "reviewComment": "Reason" }`. These
writes retain administrator Cookie+CSRF or administrator Bearer authentication;
integration credentials cannot review. Oversized comments return 400 without a
state change. Already reviewed manual requests still return 409.

The internal result boundary persists validated AI conclusions from the independent automatic worker. Each stable `attemptId`
records the actual model, review time (UTC Unix milliseconds), bounded verdict,
POS/meaning/UK-US phonetics/comment and an immutable `applied` fact. Only pending,
currently eligible submissions may change the catalog. Eligibility is checked
inside the same serialized writer/context, so the automatic lease implementation must
read its current owner/epoch/expiry there, never pass an earlier boolean. Network
calls belong outside this transaction.

AI accept keeps submitted word/POS/meaning/example and fills only currently blank
default phonetic sides, reading them inside the writer. AI revise keeps the
submitted spelling/example and uses the reviewed POS/meaning/phonetics. Its
`definitionKept` is explicitly false even when only POS or phonetics changed;
reuse and question creation use that same persisted decision. Manual approvals
retain their existing spelling/definition comparison rule. AI reject keeps all
submitted text and exposes its reason.

The catalog outcome, submission metadata and permanent conclusion commit or roll
back together. A replay of the same canonical payload and attempt ID returns the
original record/application fact; a different payload with that ID returns a
conflict. A valid conclusion arriving after manual review, another AI result or
expired eligibility is retained with `applied=false`, without changing the
submission or catalog. It must never be displayed as an applied correction.
Records keep the original AI fields after later catalog edits/deletion and are
not reconstructed from current vocabulary. They contain no raw provider answer
or credentials. Administrator history queries and separate checked markers are described below.


## Automatic review (default off)

`reviewAssistant.autoReviewEnabled` in [online settings](SystemSettings.md#review-assistant-and-enrichment)
uses `MeaningSubmissions:Ai:AutoReviewEnabled` (default `false`). Advice remains
available whenever endpoint/model are configured. Automatic review additionally
requires a configured supplementary book and a model name of at most 200 characters.
A self-hosted endpoint may use an empty API key. Saving incomplete settings is
allowed; automatic work pauses until repaired. Enabling this switch authorizes AI
to approve, revise or reject without human confirmation. Incorrect results may
enter the shared catalog; administrators accept this risk and correct entries
through the existing vocabulary editor. The administrator history tab below supports independent human verification.

Only integration submissions are candidates. The successful submission commit
emits a lossy wakeup; the response never waits for AI. Startup, lost wakeups, older
pending rows and submissions during a disabled period are recovered from SQLite.
Each scan claims at most 20 submissions, with one concurrent request and a five-second
rescan. Claims use short serialized transactions with persistent attempt ID,
owner, epoch and expiry; the provider request runs outside every database writer.
Expiry is the captured request timeout plus 30 seconds. Final eligibility reads
the current lease and pending state in the same result writer. Competing manual
review and late owners cannot change an already reviewed submission. Every
validated late result is permanently retained once with `applied=false`.

Accept keeps the submitted word/POS/meaning/example and only fills currently empty
default phonetic sides. Revise changes POS/meaning/phonetics, keeps word/example,
and always returns `definitionKept=false` with its comment. Reject preserves the
proposal and exposes its reason. The prompt requires rejecting a misspelling with
a likely intended spelling in the reason; no response field can replace the word
or example. AI accuracy is not guaranteed.

Failures keep pending and retry indefinitely: 8, 16, 32, 64, then 120 seconds
between attempts, respecting a longer valid HTTP 429 Retry-After. There is no
exhaustion, manual handover or enrichment-wide pause. Cancellation, disabling or
missing configuration pauses without consuming a failure. New network retries
use new attempt IDs. A validated bounded domain result is durably staged before
application; application failures/restarts resume its exact attempt/model/time
without another network call. An ambiguous commit replays the immutable outcome.
Unknown/lost supplier answers produce no invented conclusion; remote billing is
not guaranteed to occur exactly once.

The independent automatic client sends only word/POS/meaning/example from the
proposal, with its captured configuration/model. Headers and body share one
1–300 second cancellation budget. The completion envelope is limited to 64 KiB,
JSON depth 8, unique names, one textual choice and a complete answer. The decision
accepts only verdict/POS/meaning/UK-US phonetics/comment, with limits 32/500/100/100/500;
nullable fields must be explicitly present except accept's optional comment.
Revise needs a nonempty meaning and explanation; reject needs a nonempty reason.
Wrong types, unknown/duplicate fields, missing required values, truncation,
Markdown fences, oversize or unusable answers retry safely, never become rejection.
No redirect, cookies, tool execution, raw response, credential or private endpoint
is persisted/logged by this path. Existing manual advice parsing is unchanged.

`GET /admin/meaning-submissions` additionally returns `automaticReview`:
`{enabled, paused, pauseCode, pendingCount, lastFailureCode, lastFailureAt, nextAttemptAt}`.
This administrator-only summary counts **all** pending rows, including unqueued
ones, independent of the current list filter/page. Pause codes are `disabled`,
`configuration_missing`, `configuration_invalid`, `submissions_not_configured`.
Failure codes are `rate_limited`, `service_unavailable`, `request_rejected`,
`response_too_large`, `invalid_response`, `service_timeout`, `connection_failed`,
`result_commit_failed`. The latest safe failure remains visible after recovery;
`nextAttemptAt` is the earliest persisted pending due/lease time, or null when
none is scheduled (including a completely unqueued backlog). Times are UTC.
No supplier error/header/body or review explanation appears in this summary,
integration/ordinary DTOs or worker logs. The admin page says work recovers
automatically, without requiring manual processing. Approval still wakes the
independent catalog enrichment pipeline after commit.


## Permanent AI history and administrator checks

`GET /admin/meaning-submission-reviews` is administrator-only. Query parameters:
`page` (default 1, positive), `size` (default 20, 1–100), optional
`verdict=accept|revise|reject`, `checked=true|false`, `applied=true|false` and
`recordId` (1–100 characters). Invalid values return 400. An unknown record filter
returns an empty page. Responses contain `{items, totalCount, page, size}`; count
and page share one deferred SQLite read snapshot, ordered by review time descending
then record ID ascending. Separate requests may observe newly appended records.
GET never checks a record or invokes AI.

Each item contains `recordId`, `submissionId`, `source` (submitting client),
`reviewerKind: "ai"`, `original: {word, partOfSpeech, meaning, example}`,
`ai: {verdict, partOfSpeech, meaning, phoneticUk, phoneticUs, comment, model, reviewedAt}`,
`applied`, `checked`, nullable `checkedAt`, and nullable `resultWordId`.
Times are UTC; the model/time and structured AI fields are the original captured
conclusion, not current configuration or catalog values. The immutable submitted
text supplies `original`; runtime staging input is never a history read source.
No lease, attempt, credential, endpoint or raw provider answer is returned.
`resultWordId` links an applied approval to the existing administrator editor;
a later deletion can make that editor return 404 without losing history.

Default reads include valid conclusions that lost a race (`applied=false`). These
must not be labelled actual approvals, corrections or rejections. To find actual
AI corrections use `verdict=revise&applied=true`; accept/reject have equivalent
filters. Accept's returned AI fields are suggestions: application keeps original
POS/meaning/example and only fills blank default phonetic sides. A revise that
changes only POS or phonetics still remains an explicit revision.

`POST /admin/meaning-submission-reviews/{recordId}/checked` accepts an empty body.
It returns `{recordId, checkedAt}`. Each record has one independent first-check
fact: repeat/concurrent calls return the same original time, without updating the
permanent conclusion, submission status, catalog or AI work. Unknown records return
404. A lost response must first be confirmed with the same list GET using
`recordId`; the UI never automatically repeats an uncertain write. There is no
uncheck, batch action, undo or rollback API.

Both routes require administrator Cookie+CSRF for writes or administrator Bearer;
anonymous, ordinary and integration tokens cannot access them. Human verification
is independent of subsequent vocabulary edits/deletion and later AI conclusions.
The submission list additionally accepts optional
`reviewerKind=admin|ai|unknown` (unknown includes null legacy/pending/direct matches).
Its existing `source`, default status/paging and integration DTOs keep their meaning.
