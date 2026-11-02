# Autonomous enrichment acceptance and migration notes

This is the integration and release checklist for autonomous vocabulary enrichment.
Automated checks use synthetic chat/speech responses, a fixed MP3 and disposable
SQLite/private S3 resources. They prove control flow, protocol validation and
recovery. They do **not** prove that a supplier pronounces the intended reading,
uses the requested accent or avoids speaking directions aloud. Real listening is
pending until a maintainer supplies authorized test configuration and records it
below. Automation may be merged independently; the acceptance tracker remains open.

## Automated evidence map

Run the [required checks](Testing.md#how-tests-are-executed) from the current checkout.
Keep command outcomes and commit IDs with the review; a total test count is not
individual scenario evidence.

| Scenario | Executable assertion or output |
| --- | --- |
| HTTP configuration → hosted discovery → chat → MiniMax native or OpenAI speech → conditional MP3 upload → state and meaning playback | `EnrichmentEndToEndTests.HttpSettings_HostedWorker_Adapters_Publication_Edit_Playback_AndRestart`, once per protocol; production capability checks and clients remain active |
| Disabled/unconfigured deployment makes no AI/TTS request; unsupported controlled model stops, HTTP model correction resumes automatically | Both `EnrichmentEndToEndTests` tests, including safe administrator status and zero speech dispatch assertions |
| Empty specific inherits despite retained legacy binding; edit detaches immediately, different reading is missing until publication; repeat preserves epoch, clear inherits; other accent stays intact | The same HTTP/hosted test, plus `MeaningPhoneticAdminEndpointTests`, `MeaningPhoneticEditTests` and `AudioSelectionTests` |
| Real private S3 invalidation, reference protection, denied operation, successful Delete with lost Delete/observation results, reopen/retry, permanent receipt without resurrection | `AudioStorageProbe.VerifyInvalidationAsync`, run by `test-audio-storage.sh` |
| Actual executor chat/TTS/S3 publication commits, process is killed before task receipt recovery; reopen recovers original round/asset/receipt without another chat/TTS/upload | `EnrichmentRecoveryProbe`, run by `test-audio-storage.sh`; the crash hook is only a database interceptor in the disposable probe |
| Old executor after lease/input/epoch/ABA edit cannot write fields or bind uploaded audio; unknown upload reconciles its original key | `EnrichmentTargetExecutorTests.FinalFieldTransaction_*`, `PublicationWriter_*`, `SpecificPhoneticEditsDuringTts_*`, `LostPutResponseAndUnknownStartedCall_*`; `MeaningPhoneticEditTests` |
| Seven real adapter requests under 429 consume zero failures; fifth service failure pauses; independent probe restores service while exhausted side remains failed | `EnrichmentSchedulerIntegrationTests.SharedThrottle_*` and `FifthFailurePausesService_*`; persistent SQLite and real HTTP adapters, synthetic transport |
| Preparation expands sb./sth., resolves slash templates, separates proper-name/IPA directions; unsafe/uncertain plan cannot reach speech | `PronunciationPreparationTests`, `PronunciationSpeechTests` template/proper-name and rejection matrices; fixed audio is not a listening result |
| Historical schema upgrade preserves IDs/defaults/source revisions/receipts; no retrospective deletion authority | `MeaningPhoneticMigrationTests`, `EnrichmentMigrationTests`, `AudioInvalidationMigrationTests`, `MeaningPhoneticEpochMigrationTests`, `EnrichmentSchedulerRepositoryTests` migration coverage |
| Container recreation retains exact saved rows, plans, requests, failure budgets, throttle deadlines, cleanup authority/epochs and pending regeneration | `test-container.sh` recreates the same data mount twice and compares table snapshots; these rows are synthetic persisted fixtures, not evidence of provider execution |
| Administrator permissions/CSRF, secret boundaries, projected state, retry/history, conditional edits and media release | Host endpoint tests and `frontend/e2e/enrichment.spec.ts`, `audio-preview.spec.ts`, `enrichment-settings.spec.ts` |

The ordinary upload crash probe still terminates processes at reservation,
before Put, after Put and after a lost success response. That is separate from
the enrichment publication crash above. Old green builds and a container starting
successfully do not substitute for either new scenario.

## Release and upgrade behavior

- No new migration or business route is introduced by this acceptance change.
  The preceding pronunciation, task, invalidation, epoch and scheduler migrations
  remain required. They preserve old catalog IDs, default phonetics, assets,
  bindings and permanent upload receipts. The historical migration tests compare
  old values and fresh/upgraded schemas.
- Playback selection deliberately changes: with `meaningId`, an empty specific
  phonetic uses only the default binding, even if a legacy override remains. A
  nonempty different specific reading without its own audio returns `missing`.
  Equal nonempty IPA can use default playback fallback while its own generation
  remains pending. See the [selection table](../api/Audio.md).
- A real specific edit atomically changes that side, releases its old binding,
  invalidates old execution authority and persists exact deletion intent plus new
  work. Clearing only restores inheritance; it never fills a specific field.
  Same-value submissions do not restart generation. Default/other-accent and any
  still-referenced object remain protected. Cleanup and replacement are independent.
- Every applicable step has its own default allowance of five actual failures,
  including the first. 429 and supported HTTP-200 rate-limit codes record requests
  and shared waiting without consuming it. A successful service probe does not
  revive exhausted work. Restart, scan and image replacement do not reset rounds,
  budgets or delays. Relevant configuration/content correction or an explicit
  single-step retry can open a successor; completed work and history are retained.
- Reuse the fixed `/app/data` bind mount or named volume when replacing a container.
  Retain the complete `appsettings.json`, including private configuration-version
  metadata, `vocabulary.db`, the data-protection material and a matching private
  object backup. Anonymous volumes are not automatically attached to replacements.
  Environment and command-line values take precedence and remain read-only in the
  UI; they are not copied into the file. See [deployment](Deployment.md).
- Stop writes/workers and take consistent backups before upgrade or Down. An older
  image cannot read a newer schema (`migration.version_too_new`). Down discards the
  corresponding task/history, cleanup authority, epoch or scheduler metadata as
  documented for each migration; it does not reconstruct detached bindings or
  deleted MP3s. Restore a matched pre-upgrade database/configuration/object backup
  when rolling back. A database backup alone may reference an object already
  removed under exact specific-phonetic authority.

## Pending real listening

Use a disposable catalog and only configuration authorized for this test. Never
put API keys, signed URLs, raw supplier responses or production data in the report.
Record provider, base host, exact model, configured UK/US voice IDs and speed,
test date and application revision as nonsecret metadata. The implemented
[support matrix](../api/Audio.md#speech-adapters) is restrictive: compatible HTTP
syntax alone is not pronunciation-control support.

Complete every row for **MiniMaxNative** and for one explicitly supported
**OpenAICompatible** endpoint/model combination, each with **UK and US**:

| Sample | Prepared text and reading constraint | Heard text / reading / accent / directions spoken? | Result |
| --- | --- | --- | --- |
| Ordinary word (for example `book`) | Pending authorized preparation | Not listened | Pending |
| Polyphonic word (`lead`, distinct default and explicit meaning IPA) | Pending authorized preparation; include explicit IPA | Not listened | Pending |
| `give sb. sth.` | Must expand somebody/something | Not listened | Pending |
| Slash template (`do/does sth.`) | One natural expanded alternative, no slash | Not listened | Pending |
| Proper name (maintainer-selected known pronunciation) | Record intended pronunciation before testing | Not listened | Pending |

Listen to the actual generated result. It must speak the prepared text, follow
the explicit reading and configured accent, and omit prompts/definitions/directions.
HTTP 200, valid MP3 frames and correct request parameters cannot meet this standard.
If unsupported or uncertain, retain the safe failure and correct configuration or
plan; do not bypass capability checks or fall back to bare headword speech.

Current status: no real listening result is claimed. Next action: the maintainer
provides authorized test configuration through the normal server-side settings
and records the two protocol × two accent × sample results. Automation completion
does not complete the real-listening or parent acceptance criteria. AI correctness,
all compatible suppliers, strictly once remote billing and immediate revocation of
already issued links/downloaded copies are not guaranteed.
