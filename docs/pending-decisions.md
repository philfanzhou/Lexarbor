# Pending decisions

## PD-001: Supplemental dictionary source and redistribution license

- Status: Pending
- Tracking: [Issue #52](https://github.com/philfanzhou/Lexarbor/issues/52)
- Scope: the optional second-layer dictionary dataset and any release artifact that redistributes it
- Current evidence: ADR-002 establishes a self-authored starter book as the first layer and an external open dictionary as a separately distributed second layer. No source dictionary has been selected. Its fields will influence the supplemental schema, its size may affect packaging, and its redistribution terms require explicit review.
- Option A: select one external dictionary and model the supplemental table around that source.
- Option B: define a source-neutral supplemental schema and import each external dictionary through a conversion pipeline, allowing sources to be replaced or combined.
- Blocking scope: only the optional supplemental dictionary design and distribution. It does not block the MIT-licensed starter book, current Lexarbor functionality, authentication, storage, or deployment.
