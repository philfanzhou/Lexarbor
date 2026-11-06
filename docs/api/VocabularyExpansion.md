# Automatic vocabulary expansion

Configuring `Enrichment:Text:Endpoint`, `Model` and `ApiKey` automatically starts
missing IPA/POS completion and LLM vocabulary expansion. There is no separate
enable switch or start API. Expansion uses the existing enrichment worker,
persistent discovery, task leases, retry allowance and shared text service gate.
Speech uses the same workflow when its model, accent settings and storage are ready.

## Generation and publication

Each `Expand` task analyzes one existing meaning, without a UK/US locale. The LLM
receives the word, POS, meaning and example, plus the eleven supported
[relation types](VocabularyRelations.md#types). It proposes at most twelve common,
direct inflections/derivatives with target spelling, POS, Chinese meaning, English
example and an applicability note. Uncertain candidates must be omitted; an empty
result is a durable completed analysis. Explicit phrase positions are excluded.

The application validates strict JSON structure, required fields, lengths, relation
codes and duplicate word/type pairs. It rejects truncated, oversized, malformed,
credential-reflecting or refused responses. Schema validation does not prove
linguistic correctness: LLM-generated content may need later administrator editing.
No raw provider response, credentials or provider error text is stored or logged.

The destination is the same enabled book configured by `MeaningSubmissions:BookId`.
If it is missing or disabled, expansion waits without spending a model call;
field completion and audio work remain independent. A previously existing target
word is reused without adding/replacing its meanings or memberships. A new word
gets a meaning in the supplement, never in the source textbook. Existing relations
retain their notes, provenance and confirmation state; new relations are confirmed
with `originKind=generated` and the task ID as `originReference`. A self-relation
is allowed for invariant inflections, but not for a same-spelling derivative.

All new words, meanings, relations, generated-word provenance and completion commit
atomically after source-version, task-lease and configuration checks. A failed
commit rolls them back. Validated plans are persisted before publication, allowing
restart recovery without another model call. A remote outcome lost before staging
may require another call; exactly-once provider billing is not guaranteed.

## Subsequent work

New words automatically enter ordinary IPA/POS/audio discovery. They are marked as
generated and do not recursively generate more words. Existing independent source
words remain eligible for their own analyses. This bounds expansion to one direct
generation from catalog sources, avoiding self-sustaining word-generation loops.

Completed analyses, including empty results, are not repeated on every scan,
restart, or model/credential rotation. Changing source spelling, POS, definition
or example causes discovery to open a new round. Previously published content is
retained; ordinary word edits still invalidate affected relation confirmations.
Relation applicability notes describe a sense but are not machine-readable quiz
grading rules. Existing quiz types and student book-selection behavior do not change.

## Observation and recovery

Use the existing [enrichment APIs](Enrichment.md) and `/#/enrichment`, filtering
`step=Expand`. Expansion has `locale=null`; an accent filter excludes it. The same
task detail, safe failure history and failed-step retry apply. Throttling uses the
shared text gate without consuming failure allowance; transport failures use the
existing service pause/recovery behavior. Invalid semantic/structured results stop
the task rather than publishing partial content.

The model connection remains server-only. Existing administrator authorization and
CSRF rules protect settings and retry; no new public write or model execution API
is introduced. See [deployment and rollback](../development/Deployment.md#vocabulary-expansion-and-model-driven-activation-upgrade).
