# On-demand learning questions

Lexarbor selects and generates one complete question for a caller's target meaning.
The catalog and submission routes use the same selection and letter-hiding rules.
The single-question, batch-question and submission `/question` routes share the
[business API key requirements](BusinessApiKeys.md). No student ID, history, question batch, answer or schedule is stored.

## Routes and requests

| Route | JSON body | Access |
| --- | --- | --- |
| `POST /api/vocabulary/learning-question` | Required `wordId`, `bookId`, `meaningId`, `mode`; optional `unitId`, `sameEntryKind` | `X-Api-Key` with `catalog` scope |
| `POST /integration/meaning-submissions/{id}/learning-question` | Required `mode` | `X-Api-Key` with `meaning-submissions` scope; configured `MeaningSubmissions:BookId` |

`mode` is exactly `new` or `review`. IDs must be nonblank. The catalog meaning
must belong to both the requested word and book. A supplied nonblank `unitId`
must exist in that book; the target need not itself be assigned to that unit.
`sameEntryKind` defaults to false. These two optional fields constrain choice
candidates exactly as on the existing single-question route: scope is the unit,
or the whole book when omitted; a phrase position makes the target a phrase,
otherwise it is a word. When the configured submission supplement or the active
managed expansion supplement has too few choice candidates, Lexarbor fills the
remaining options from enabled books. The managed supplement is identified by
its stored expansion-destination record. Ordinary books keep their own scope.
The same rule applies to single and batch catalog questions.

A pending submission uses its original display spelling and meaning, with choice
candidates from all enabled books. An approved submission with `definitionKept`
uses the approved catalog target, including its current book status and candidate
rules. Rejected and changed submissions are unavailable. AI `revise` remains
unavailable even when only the part of speech or pronunciation changed; display
text equality does not override `definitionKept`.

### Catalog example

```http
POST /api/vocabulary/learning-question
X-Api-Key: <issued-api-key>
Content-Type: application/json

{"wordId":"word-explode","bookId":"book-1","meaningId":"meaning-explode","mode":"new","sameEntryKind":true}
```

```json
{
  "success": true,
  "data": {
    "type": "EnglishToChinese",
    "prompt": "Explode",
    "options": [
      {"text": "河流", "isCorrect": false},
      {"text": "爆炸", "isCorrect": true},
      {"text": "云", "isCorrect": false},
      {"text": "石头", "isCorrect": false}
    ]
  }
}
```

### Submission example

The authorization header contains the caller's existing short-lived integration
token. For a pending submission whose display spelling is `Explode`:

```http
POST /integration/meaning-submissions/submission-explode/learning-question
X-Api-Key: <issued-api-key>
Content-Type: application/json

{"mode":"review"}
```

```json
{
  "success": true,
  "data": {
    "type": "FillLetters",
    "prompt": "爆炸",
    "pattern": ["E", null, "p", null, "o", null, "e"],
    "answerWord": "Explode"
  }
}
```

Examples show one possible ordering and pattern, not stable output.

## Selection and response contract

- `new` attempts only `EnglishToChinese`. Insufficient choices fail the request;
  this mode never falls back to letter filling.
- `review` selects uniformly among types that can actually be generated:
  `EnglishToChinese`, `ChineseToEnglish`, and eligible `FillLetters`. A uniform
  random ordering tries each type at most once, stopping at the first available
  one. Only an explicit lack of distinct choice candidates excludes a choice
  type. Target/source errors, authentication, storage faults and cancellation
  are never reasons to try another type.
- With three available types each has probability 1/3; with two, each has 1/2.
  Requests have no daily quota, history exclusion, rotation or guaranteed finite
  coverage. They need not produce the same question or a different question.
- `FillLetters` reads the already validated target text directly. It queries no
  choice candidates and works even when there are no distractors at all.

Successful responses are `{"success":true,"data":...}`. `data` has exactly one
of the following structures, with a nonblank `prompt`:

| Type | Fields |
| --- | --- |
| `EnglishToChinese` | `type`, English `prompt`, `options` |
| `ChineseToEnglish` | `type`, Chinese `prompt`, `options` |
| `FillLetters` | `type`, Chinese `prompt`, `pattern`, `answerWord` |

Choices have exactly four distinct nonblank `text` values and exactly one
`isCorrect:true`. Every option carries an explicit JSON boolean, including false.
Choice data contains neither `pattern` nor `answerWord`; fill data contains no
`options`. There is no persistent question ID, student identity or timing field.
`type` is a string for future extension; these are the only three current values.

### Letter hiding

The rules are migrated from Ruoyu.Study commit
`45bcc7c51d1df61ec0c8bde5afceac7a8600a711`, `LetterHiding.cs` and its vocabulary
tests, without normalization or extra separators:

1. Count Unicode scalars with `Rune.IsLetter`, rather than UTF-16 code units.
   At least three letters and one hideable letter are required.
2. Only ASCII space and hyphen start a segment. Keep the first letter of each
   segment visible. Punctuation, combining marks and other nonletters stay visible.
3. Hide total letters × 0.4, rounded with `MidpointRounding.AwayFromZero`, clamped
   to at least one and at most the hideable count. Sample uniformly without replacement.
4. `pattern` has one element per answer scalar: null for each hidden letter,
   otherwise the exact original scalar string. It always has at least one null.
   `answerWord` preserves original spelling, case and nonletters.

`explode` hides three letters; `go` cannot produce `FillLetters`. An ineligible
fill type leaves the two choice directions available for review when they have
enough candidates.

## Errors

These two deployed routes translate their business failures locally into
`{"success":false,"code":"...","message":"..."}` with the matching HTTP status:

| HTTP | `code` | Fixed `message` |
| --- | --- | --- |
| 400 | `QUESTION_INVALID_REQUEST` | A valid question request with mode new or review is required. |
| 404 | `QUESTION_TARGET_NOT_FOUND` | The question target was not found. |
| 422 | `QUESTION_SOURCE_UNAVAILABLE` | The question source is unavailable. |
| 422 | `QUESTION_UNAVAILABLE` | No allowed question type is available. |
| 503 | `QUESTION_SERVICE_UNAVAILABLE` | The question service is not configured. |

404 covers a missing target, wrong word/meaning/book ownership, a missing or
foreign unit, and a submission no longer eligible for questions. Disabled books
answer source-unavailable, independently of candidate availability. A valid
source with no generatable allowed type answers question-unavailable.

401/403 retain the existing authentication middleware format and meaning. Rate
limits and infrastructure failures retain [global Problem Details](../development/ErrorHandling.md);
internal exceptions never become target-not-found or a successful fallback.
A normal 404 from an unknown or undeployed route has no `QUESTION_TARGET_NOT_FOUND`
code. Consumers must inspect both the HTTP status and explicit business code
before skipping a target, rather than treating every 404/422 as skippable.

## Caller and rollout responsibilities

Study validates student ownership, signs and saves its own question/answer
snapshot, hides answers until appropriate, and handles expiry, replay, grading
and scheduling. Responses include correct answers and must not be forwarded
unchanged to a student's browser. Use the API key from the Study backend. Pending submissions are available
through the authenticated submission routes.

There are no new settings, migrations, dependencies or persistence directories.
Validate against this PR's source/build. After merge, the release/deployment owner
records the first supporting revision or image and shares it with the Study task.
Study can implement and test against the merged contract in an isolated Lexarbor
build. Deploy compatible Lexarbor before switching the running Study consumer.
Production deployment and Study implementation are separate work, not PR merge
prerequisites. See [Deployment](../development/Deployment.md#on-demand-learning-question-rollout).
