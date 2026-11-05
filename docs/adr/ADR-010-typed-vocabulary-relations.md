# ADR-010: Typed relations between global vocabulary entries

- Status: Accepted

## Decision

Store each directed word-level fact once in `vocabulary_relation`, referencing
the existing global vocabulary table at both ends. The unique identity is
`(source_word_id, target_word_id, type)`. Do not copy source/target part of speech
or attach the relation to a book-specific meaning. Self-relations and multiple
types between a pair are valid. The initial fixed type catalog covers English
inflections and derivations; no transitive relations are inferred.

Keep applicability notes, initial provenance, confirmation state, maintenance
version and UTC millisecond timestamps on the relation. Provide administrator
queries and conditional maintenance, with atomic batch creation. The API and
database enforce structural integrity; explicit confirmation owns linguistic
correctness. Usage notes are not automated grading rules.

## Consequences

- Reverse lookup returns the same record; no mirrored relationship needs repair.
- Different parts-of-speech uses of one pair/type share a word-level relation.
- Existing translation APIs, vocabulary imports and book memberships retain
  their contracts; relations are managed through separate administrator routes.
- Substantive word replacement invalidates incident confirmations atomically.
- Relations are retention references, including when awaiting review. Cleanup
  retains referenced words after their last meaning is removed and reports
  retained and deletable counts separately from the existing orphan count.
- Deleting a relation never implicitly deletes a word. Initial provenance and
  last confirmation do not constitute a complete edit-history ledger.
- The migration adds one empty table and indexes. No vocabulary or relation data
  is bundled. Downgrading drops relationship data and must follow the documented
  backup procedure. Relationship UI and assessments remain separate work.

See [the API contract](../api/VocabularyRelations.md).
