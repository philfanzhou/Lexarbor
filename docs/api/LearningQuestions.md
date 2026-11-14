# On-demand learning questions

Lexarbor selects and generates one complete question for a caller's target meaning.
The catalog and submission routes share regular question types and letter-hiding
rules. Catalog review also uses vocabulary relations.
The single-question, batch-question and submission `/question` routes share the
[business API key requirements](BusinessApiKeys.md). No student ID, history, question batch, answer or schedule is stored.

## Routes and requests

| Route | JSON body | Access |
| --- | --- | --- |
| `POST /api/vocabulary/learning-question` | Required `wordId`, `bookId`, `meaningId`, `mode`; optional `unitId`, `sameEntryKind`, `relation` | `X-Api-Key` with `catalog` scope |
| `POST /integration/meaning-submissions/{id}/learning-question` | Required `mode` | `X-Api-Key` with `meaning-submissions` scope; configured `MeaningSubmissions:BookId` |

`mode` is exactly `new` or `review`. IDs must be nonblank. The catalog meaning
must belong to both the requested word and book. A supplied nonblank `unitId`
must exist in that book; the target need not itself be assigned to that unit.
`sameEntryKind` defaults to false. These two optional fields constrain ordinary
choice candidates as on the single-question route: scope is the unit,
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

For a pending submission whose display spelling is `Explode`:

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
- `review` selects uniformly among types that can actually be generated. The
  regular candidates are `EnglishToChinese`, `ChineseToEnglish`, and eligible
  `FillLetters`. An eligible catalog relation adds up to three types. Catalog
  review also includes `PartOfSpeechChoice` when it can be generated. A uniform
  random ordering tries each type at most once, stopping at the first available
  one. Target/source errors, authentication, storage faults and cancellation
  propagate.
- With three available types each has probability 1/3. With all seven, each has 1/7.
  Requests have no daily quota, history exclusion, rotation or guaranteed finite
  coverage. They need not produce the same question or a different question.
- `FillLetters` uses the validated target text and works even if there are no
  translation distractors.

Successful responses are `{"success":true,"data":...}`. `data` has exactly one
of the following structures, with a nonblank `prompt`:

| Type | Fields |
| --- | --- |
| `EnglishToChinese` | `type`, English `prompt`, `options` |
| `ChineseToEnglish` | `type`, Chinese `prompt`, `options` |
| `FillLetters` | `type`, Chinese `prompt`, `pattern`, `answerWord` |
| `FormChoice` | `type`, source spelling `prompt`, `relationType`, `options` |
| `RelationChoice` | `type`, target spelling `prompt`, `anchorWord`, `options` |
| `FormFill` | `type`, target meaning `prompt`, `anchorWord`, `relationType`, `pattern`, `answerWord` |
| `PartOfSpeechChoice` | `type`, target meaning `prompt`, `anchorWord`, `partOfSpeech`, `options` |

Choices have exactly four distinct nonblank `text` values and exactly one
`isCorrect:true`. Every option carries an explicit JSON boolean, including false.
Choice data contains neither `pattern` nor `answerWord`; fill data contains no
`options`. There is no persistent question ID, student identity or timing field.
`type` is a string. Catalog review clients must support `PartOfSpeechChoice`.
Clients that send `relation` in review must also support the three related
types. The submission route uses the three regular types and its existing choice
selection rules.

## Catalog relation practice

```http
POST /api/vocabulary/learning-question
X-Api-Key: <issued-api-key>
Content-Type: application/json

{"wordId":"went-id","bookId":"book-id","meaningId":"went-meaning-id","mode":"review","relation":{"sourceWordId":"go-id","type":"past_tense"}}
```

`relation` is optional. When present, it must be an object with a nonblank
`sourceWordId` and one of the [eleven relation codes](VocabularyRelations.md#types).
Malformed `relation` returns `400` with `QUESTION_INVALID_REQUEST` in both modes.
A confirmed relation from `sourceWordId` to `wordId` makes related types eligible
in `review`. A missing, pending or reverse-only link leaves the other eligible
review types in the draw. In `new`, the relation is validated
and the question follows the usual English-to-Chinese rules.

`FormChoice` asks for the target spelling given the source and relation type.
Wrong options can include the source spelling, its other confirmed targets,
and words that share a confirmed source with it. The draw removes
duplicate spellings and every other spelling that answers the same source and
relation type. This type requires three distinct wrong options.

```json
{"success":true,"data":{"type":"FormChoice","prompt":"go","relationType":"past_tense","options":[{"text":"gone","isCorrect":false},{"text":"went","isCorrect":true},{"text":"going","isCorrect":false},{"text":"goes","isCorrect":false}]}}
```

`RelationChoice` asks for the relation between the source and target. The four
options use the codes from either the four derivations or the four verb forms
(`past_tense`, `past_participle`, `ing_form`, `third_person_singular`). A second
confirmed answer in the same group makes the question unavailable. Clients
localize the relation codes when presenting the choices.

```json
{"success":true,"data":{"type":"RelationChoice","prompt":"went","anchorWord":"go","options":[{"text":"ing_form","isCorrect":false},{"text":"past_participle","isCorrect":false},{"text":"past_tense","isCorrect":true},{"text":"third_person_singular","isCorrect":false}]}}
```

`FormFill` shows the target meaning and the source spelling. It preserves the
longer of the two words' case-insensitive common prefix and suffix, preferring
the prefix on ties. If neither end is shared, it keeps the first letter visible.
It hides the remaining letters and keeps all nonletters visible. The pattern
has one entry per Unicode scalar and requires at least one hidden letter.

```json
{"success":true,"data":{"type":"FormFill","prompt":"去了","anchorWord":"go","relationType":"past_tense","pattern":["w",null,null,null],"answerWord":"went"}}
```

### Family distractors for catalog review

Both translation directions prefer words from enabled books that are directly
linked to the target by a confirmed relation in either direction, or share a
source with it. The shared-source step is one level deep and includes inflections
and derivatives. For example, `gone` and `going` are candidates for `went`
when all three have confirmed relations from `go`. A `relation` request field
is optional for this behavior.

English-to-Chinese draws up to one eligible current definition per candidate
spelling and excludes other definitions of the target spelling from enabled
books. Chinese-to-English excludes any spelling with the stem definition in
an enabled book. Duplicate choice text is compared after trimming and ignoring
case. The family pool spans enabled books. Vacant choices use the ordinary
book, unit, entry-kind and supplementary-book fallback rules.

## Part-of-speech choice

Lexarbor automatically considers this type for catalog `review`, with or
without `relation`. `new` still draws only `EnglishToChinese`.

```http
POST /api/vocabulary/learning-question
X-Api-Key: <issued-api-key>
Content-Type: application/json

{"wordId":"happily-id","bookId":"book-id","meaningId":"happily-meaning-id","mode":"review"}
```

```json
{
  "success": true,
  "data": {
    "type": "PartOfSpeechChoice",
    "prompt": "快乐地",
    "anchorWord": "happiness",
    "partOfSpeech": "adverb",
    "options": [
      { "text": "happy", "isCorrect": false },
      { "text": "happily", "isCorrect": true },
      { "text": "cheerful", "isCorrect": false },
      { "text": "sadly", "isCorrect": false }
    ]
  }
}
```

Use `anchorWord` as the clue and `prompt` as the target definition. Localize
`partOfSpeech` using its code: `noun`, `verb`, `adjective`, `adverb`, `pronoun`,
`preposition`, `conjunction`, `determiner`, `article`, `numeral` or `interjection`.
Common single labels such as `n.`, `vt.`, `adj.` and `adv.` map to those codes.
The requested `wordId`, `bookId` and `meaningId` still determine the answer.

The clue must have a different spelling and a recognized, different part of
speech in an enabled book. It can come from either end of a confirmed derivative
relation or from a sibling linked by confirmed derivative relations to a shared
source. Wrong options first come from the target's confirmed family in enabled
books. The ordinary book, unit, entry-kind and supplementary-book rules fill any
remaining slots. The clue, duplicate spellings and alternatives matching the
definition and part of speech are excluded. A missing or unrecognized label on
the same definition also makes that alternative unsafe. If the target part of
speech is unclear, another family spelling could answer, the clue is missing or
fewer than three distinct wrong options remain, the draw tries another type.

## Letter hiding

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

When upgrading an existing integration, deploy a client that accepts
`PartOfSpeechChoice` before routing catalog review requests to a Lexarbor build
that returns it. Keep sending the existing request fields during the upgrade.
After Lexarbor supports `relation`, clients that use the three related types
can send that field. The feature uses the current database and configuration.
The deployment owner shares the supporting revision or image with the client
owner so both sides can validate the contract before switching traffic.
