# Lexarbor administration frontend specification

## Stack and boundaries

- Vue 3.5, TypeScript, Vue Router, Element Plus, Axios, Vite.
- No additional state management or authentication dependency.
- `read-excel-file` and `fflate` read `.xlsx` files on the batch import page, only inside a worker that is loaded when such a file is chosen ([ADR-006](../adr/ADR-006-batch-import-file-formats.md#excel)).
- The frontend is hosted by the Vocabulary backend and is same-origin with the administration API in production.
- The frontend calls relative `/admin/*` paths only and does not know the Identity address.
- The frontend never stores or reads an access token, refresh token, AppSecret, default administrator account, or password.

## Routes

| Path | Access | Page |
|------|----------|------|
| `/login` | Anonymous | Password form or SignaCore hosted navigation, per the deployment's login mode |
| `/forbidden` | Anonymous | Non-administrator notice |
| `/books` | Administrator | Vocabulary book management |
| `/books/:bookId/words` | Administrator | One book's word list |
| `/vocabulary` | Administrator | Whole-library word list |
| `/phrases` | Administrator | Phrase positions by book, unit, section, and keyword |
| `/import` | Administrator | Word import |
| `/import/batch` | Administrator | Mixed vocabulary batch import (legacy default) |
| `/import/batch/words` | Administrator | Explicit word batch import |
| `/import/batch/phrases` | Administrator | Explicit phrase batch import |

The application uses hash history. On first entry to a protected page it calls `GET /admin/auth/session` to restore the cookie session; an unauthenticated response redirects to `/login` and a 403 redirects to `/forbidden`. While unauthenticated, neither the administration navigation nor any actionable page is rendered. The hosted-login callback and logout-return routes re-enter the application at these hash routes: a successful callback lands on the stored return page, where the ordinary restore establishes the session, and their fixed failures land on `/login?reason=…`, which the login page renders as one notice.

## Authentication API

| Method and path | Request and response |
|------------|-----------|
| `GET /admin/auth/start` | Reached only as a top-level browser navigation, never XHR, with an optional allowlisted `returnUrl`; the backend orchestrates the whole provider round trip |
| `GET /admin/auth/session` | Returns the current administrator session |
| `POST /admin/auth/logout` | Deletes the server-side cookie; the frontend always clears its local state. Hosted mode may answer `{ logoutUrl }`, which the browser is then navigated to; without it the logout was local-only |

Axios uses `withCredentials=true`. Administration write requests send:

```text
X-Requested-With: XMLHttpRequest
```

No Authorization token, localStorage token, or sessionStorage token may be added.

## State and errors

Authentication state lives in a small in-project TypeScript module or composable that maintains:

```text
isAuthenticated
currentUser
restoreSession()
logout()                // resolves with { logoutUrl? }
clearSession()
```

A shared `ApiError` carries the public message, an optional HTTP status, and, only for the batch import route, an optional `errors` list of `{ index, message }` per-entry failures. `errors` is filled only when every item has an integer `index` and a string `message`; any other shape leaves it undefined.

| Status | Frontend behaviour |
|------|----------|
| 400 | Show the parameter or form error |
| 401 | Clear the session and redirect to the login page |
| 403 | Redirect to the forbidden page |
| 404 | Show that the resource does not exist |
| 409 | Show the data conflict; for a book deletion, suggest disabling it instead |
| 413 | Show that the request body is too large and should be split |
| 422 | Show that the business precondition is not met |
| 500/502/503 | Show the generic service error |

Components use `catch (error: unknown)` with the shared conversion function, never `any`.

The Axios response interceptor runs the global 401/403 handling before any component's `catch`. A request may carry an optional "still current" guard (`lxIsCurrent`); when it returns false — the request belonged to a session that has since ended — the interceptor skips that handling for this request only, so a late 401/403 cannot clear or redirect the session that replaced it. Every request without the guard keeps the default behaviour.

## Hosted login mode

SignaCore's hosted pages are the only administrator sign-in, so the login page renders exactly one sign-in action and never asks for a password. The frontend orchestrates navigations only; every protocol value — state, nonce, PKCE verifier, code, access token, ID token, client secret — stays server-side and never enters a browser request, the URL the app controls, or web storage.

The **使用 SignaCore 登录** button runs a top-level navigation to this site's `GET /admin/auth/start`, forwarding the current `redirect` query as `returnUrl` only when it exactly matches the backend's route allowlist (the six fixed routes, or `/books/<id>/words` with a safe id); otherwise the parameter is omitted and the backend default applies. One activation begins exactly one navigation: the button turns busy and a second activation is a no-op. The provider round trip and the callback are backend-driven; on success the browser lands on the stored return page, where the ordinary route guard's `GET /admin/auth/session` restore establishes the username, roles, and the one version request.

The login page consumes the six fixed `reason` values of the callback and logout-return routes as exactly one notice each, and ignores an unknown value harmlessly:

| `reason` | Notice |
|------|----------|
| `canceled` | The provider sign-in was canceled and can be started again |
| `denied` | The account does not carry the administrator role |
| `sign_in_failed` | The sign-in failed and can be retried |
| `provider_unavailable` | The provider is temporarily unavailable |
| `logged_out` | Signed out |
| `logout_failed` | Lexarbor signed out, but the provider sign-out was not confirmed complete; it may still hold a session |

Logout navigates in hosted mode. When the answer carries `logoutUrl`, the browser is handed to that one-time provider URI as a top-level navigation and no local navigation may race or override it; the provider then returns the browser to the fixed return route. When `logoutUrl` is absent, the local session has still ended, and the UI says that the identity provider may still hold a session — it never claims SignaCore signed out and never echoes upstream error text. Password-mode logout keeps its previous silent return to the login page, and a failed logout still shows the error and returns to the login page in every mode.

The mode is deployment state cached in `authState` (`authMethod`): it is prefetched best-effort when a session is applied so the logout classification does not race, it survives a logout so the login page renders immediately, and a late answer writes only this value, so it can never restore a signed-out session. Nothing is written to localStorage or sessionStorage in any mode.

## Build version display

Signed-in administrators see the running backend build beside the `Lexarbor` brand: `v1.2.3` for a release (a prerelease suffix is preserved, as in `v1.3.0-rc.1`), `edge · a1b2c3d` for a `main` build (`edge · 提交未知` when no commit was recorded), and 开发版本 for a development build. A pending fetch reads 版本获取中, and a failed, timed-out, or unreadable fetch — including a `version` of `unknown` — reads 版本未知; none of these block or disturb the page, and the fetch is not retried within the session.

The label is a quiet button that opens a small dialog listing the full `version`, the complete `revision` SHA (or 未知 when it is null), and the raw `channel`. It works from the keyboard and from touch — not only a hover or tooltip — closes on Escape and on an outside click, and keeps focus on the button that owns it. All values are rendered through Vue interpolation, never `v-html`.

The data comes from the authorized `GET /admin/system/version` through the same Axios client. Exactly one non-blocking request is sent per administrator session generation: when a login or a page load's session restore accepts a session. Route changes send nothing, the route guard's restore of a freshly created session does not send a second one, and a browser reload fetches again. When a logout begins — or a session is cleared for any other reason — the generation ends: the in-flight request is aborted and marked stale, so its late success cannot paint the header and its late 401/403 cannot clear or redirect the session that replaced it, even for the same username signing back in. Nothing is persisted to localStorage or sessionStorage; anonymous and forbidden pages never request the version.

## Existing pages

- Book management keeps search, paging, create, edit, status toggle, and delete, plus the two cleanup entries described under [Book cleanup](#book-cleanup).
- Every book row also offers 单元, which opens the unit-management dialog described under [Book units](#book-units), for disabled books as well as enabled ones.
- The administration list contains both enabled and disabled books.
- Deleting a book that still has meanings answers 409 from the plain delete, which now points at 清空内容 or 删除教材及内容; the plain delete itself stays reference-safe and never cascades.
- Word import keeps the book, word, British phonetic, American phonetic, part of speech, definition, and example sentence fields.
- The import page's book picker reads `GET /api/vocabulary-books/all`, which is unpaged and enabled-only. The paged administration search is the wrong source for a picker: called with no paging parameters it answers the default first page of twenty books, and explicit paging still answers one page, so a deployment with more than one page of books would silently lose the rest. The administration list also offers disabled books, which the import endpoint then refuses with a 422.
- Both existing features must remain usable after a successful login.

## Vocabulary list pages

`/phrases` (短语管理) reads `GET /admin/vocabulary-books/{bookId}/phrase-positions` after an administrator selects a book. The book picker uses remote keyword search and pages of 20, includes disabled books, and retains the selected name when browsing other pages. Book-list requests, unit requests, and position requests have separate cancellation and generation guards, including on unmount. Optional unit, section (`A`, `B`, `none`), and spelling keyword filters reset the page. The table counts phrase positions, so one meaning in two units or sections appears twice; unclassified and word-only assignments never appear. The table columns are unit, section, phrase, meaning, and actions; the meaning column gets the widest share. UK/US phonetics and part of speech are not table columns — the endpoint still returns `phoneticUk`, `phoneticUs`, and `partOfSpeech`, and the word detail drawer still shows them. Page capacity offers 10/20/50/100 positions, total count, and a page jumper; filters and capacity reset to page one, while a removal or reclassification beyond the new last page reloads that valid page. Reset filters retains the book and clears unit, section, and keyword; clearing the book cancels reads, clears positions and units, and returns to the selection guide. Empty books and filtered misses differ from retryable failures. The table can scroll internally on narrow screens, with fixed row actions. A row opens the existing word detail drawer, which restores focus to its trigger or the book picker if the trigger no longer exists. Empty, failed, and loading reads have distinct states and failed book or unit loads can be retried. Switching books clears the unit and section, and late results for the previous book are discarded. `e2e/phrase-positions.spec.ts` covers remote book paging/search, the five table columns (phonetics and part of speech hidden even when returned, still shown in the detail drawer), retained labels, reset/clear, capacity and page requests, last-page recovery, focus, stale reads, and 1440px/768px layout/accessibility. The Add phrase link opens the dedicated `/import/phrase` form.

Each exact phrase row also offers Adjust position and Remove from this unit. The detail drawer offers the same actions for every position tag, including word and unclassified positions absent from the phrase list. The shared dialog shows the source book, unit, section, kind, spelling, and meaning. It loads destination units from the same book, including disabled books, and lets the administrator choose a unit, Section A/B/none, and word/phrase/unclassified. A move sends complete `from` and `to` keys to `PUT /admin/vocabulary-books/{bookId}/meanings/{meaningId}/positions`, with JSON `null` for absent dimensions. Exact removal sends both `section` and `entryKind` query parameters to `DELETE .../positions/{unitId}`, using `none` for absent dimensions. No category is inferred from spelling or part of speech. These actions preserve the meaning and every other position; spelling and phonetics remain shared across books and use their separate edit flow.

The Remove from this unit confirmation names the book, unit, section, kind, spelling, and meaning and commits nothing when cancelled. A successful operation rereads the detail and current list and counts from the server; a filtered row may disappear, and an emptied last page moves to the last valid page. A 409 retains the move draft and explains the conflict, a 404 offers a fresh read, and a 400 shows the server reason. If no response arrives, the outcome is unknown: the UI rereads the positions before the administrator decides whether to retry. Switching targets or closing the dialog invalidates its draft and late responses. The existing per-meaning deletion removes the entire meaning and all its positions in that book, through the separate cleanup preview and confirmation.

Two list pages consume the administration vocabulary queries (`GET /admin/vocabulary`, `GET /admin/vocabulary/{wordId}`, `GET /admin/vocabulary-books/{bookId}/content`, and `GET /admin/vocabulary-books/{bookId}/units/{unitId}/content`), which unlike the public endpoints include disabled books and historical words with no book at all.

`/books/:bookId/words` is reached from 教材管理's 查看单词, for disabled books as well as enabled ones. It shows the book's name and status and two whole-book counts — 去重单词 (deduplicated words) and 释义 (meanings) — which keep counting the whole book while a keyword narrows the matching page. The table shows each word once with its phonetics and this book's meanings; the keyword, page, and size run on the server. 返回教材列表 leads back to `/books`. A book deleted elsewhere is a distinct notice with the same way back, not an empty word list. Each row offers 移除, and a checkbox column plus 从本教材移除（N） removes the checked rows of the current page: the selection lives in page memory only, is bounded by the page size (the cleanup API accepts at most one hundred word ids), and is dropped whenever the page, the keyword, the unit view, or the book changes, so it never silently grows into the whole book.

A unit picker (筛选单元, `.toolbar__unit`) sits beside the keyword search. It offers 全书, the default, and the book's units in unit order, loaded from `GET /admin/vocabulary-books/{bookId}/units` when the page opens and again whenever the book changes, with a late answer for a replaced book dropped. The choice is page state like the keyword — it never enters the route — and a book switch resets it to 全书. A picked unit swaps the read for the unit content route: the counts and the table then speak in unit scope (the meanings column is headed 本单元释义 and holds only the meanings assigned to the unit, and 去重单词/释义 are the unit's own counts, ignoring the keyword as before), while the keyword, paging, and the whole-book removal contract stay as they are. Inside a unit view two more pickers narrow further. The section picker (筛选分节, `.toolbar__section`) offers 全部分节 by default, or Section A, Section B, or 未分节, sent as the read's `section` query parameter (`A`/`B`/`none`); the entry-kind picker (筛选类别, `.toolbar__kind`) offers 全部类别 by default, or 单词, 短语, or 未分类, sent as the read's `entryKind` query parameter (`word`/`phrase`/`none`). The two are independent dimensions of one position and combine by intersection; the counts follow the narrowing — still per distinct meaning, so a meaning in both sections or under both kinds counts once — while the per-section and per-kind place counts beside them (分节 A n · B n · 未分节 n from the response's `sectionCounts`, and 单词 n · 短语 n · 未分类 n from its `entryKindCounts`) always report the whole unit. Both choices are page state too, reset by a unit switch and by a book switch, and they exist only inside a unit view. Switching the unit, the keyword, the page, or the book shares the page's request generation and aborting, so an answer for a replaced view cannot paint the current one. A unit deleted while its view is open answers 404 with its own notice — 该单元不存在或已被删除 — offering 返回全书 (and 返回教材列表 as everywhere); returning finds the whole book, or its own 404 notice if the book went with it. An empty unit is its own empty state (该单元暂无单词), and a failed unit read keeps the shared failure shape with 重试. The unit list itself failing is a retryable error beside the picker (重试单元列表) that disables the picker while the whole-book view keeps working.

Both removal entries — the row's 移除 and the toolbar's 从本教材移除（N） — remove whole-book meanings, so both are disabled in a unit view with a tooltip saying why; the whole-book view keeps them as they were. The shared detail drawer's per-meaning 删除整条释义及其全部位置 remains available in either view and uses its whole-book preview-and-confirm flow.

`/vocabulary` (单词管理, in the 教材 navigation group) lists the whole library: each word once, with every book membership as a tag, disabled books marked 停用, and unassigned words showing 无教材归属. Its book filter is a remote-search select fed by the paged administration book search (`GET /admin/vocabulary-books`, disabled books included) with its own pager in the dropdown footer, so deployments with more than one page of books still reach the later pages; a filter selects words by membership without hiding their other books. The import pages' enabled-only picker is untouched.

Both pages open the same detail drawer (`VocabularyDetailDrawer`): the shared word and phonetics once, then meanings grouped per book with each book's status, an unassigned word showing 无教材归属 and no meanings. Each meaning shows its unit assignments as one tag per assigned place (unit number, title, Section A/B or none, and word/phrase/unclassified kind), in the unit order the server returns; a meaning that holds two places of one unit — its Section A and its Section B — shows two tags of that unit, and one under both kinds of a place shows two tags of that place; a meaning with no assignment shows none, and the whole-book scope tags stay as they were whatever view the drawer was opened from. The drawer opens from the keyboard and from a pointer, closes on Escape and the close button, and returns focus to the button that opened it. Loading, a failed load with 重试, a missing word (404), and an empty result are distinct states. Each meaning carries a 删除整条释义及其全部位置 entry that starts the meaning-deletion flow under [Book cleanup](#book-cleanup).

Two independent edit groups live in the drawer. 编辑拼写与音标 opens the shared spelling and phonetics as one full replacement (`PUT /admin/vocabulary/{wordId}` sends `word`, `phoneticUk`, and `phoneticUs` every time — a blank phonetic is sent as `null` to clear it, and keeping a value means sending it again, unlike the import merge). Saving first confirms, naming every referencing book including disabled ones, because the change reaches all of them. Each meaning's 编辑 opens its own form (`PUT /admin/vocabulary-books/{bookId}/words/{wordId}/meanings/{meaningId}` sends `partOfSpeech`, `meaning`, and `example`) and changes only that book-owned record, in disabled books as well; ownership cannot move. An unassigned word has the shared area only. Each group saves alone with its own loading, error, and success state, one request at a time; a combined save is never implied. A definite refusal keeps the draft: 400 shows the server reason, 409 explains the conflict and offers an explicit 重新加载, and 404 reports the target is gone. An answer that never arrives is an unknown outcome: the drawer re-reads the current detail and never replays the save. A successful save re-reads the detail and the list page refreshes with it. Drafts belong to the word that opened them; switching or closing the target drops them, and a late save answer cannot land on the new target.

Each list and the drawer keep one target generation: switching a book, a word, or leaving the page aborts the in-flight request and invalidates its generation, so a late success or failure cannot paint the target that replaced it, and a retry only re-reads. Authentication failures keep the shared interceptor's clearing and redirect.

## Book cleanup

Every cleanup entry — 教材管理's 清空内容 and 删除教材及内容, the book word list's 移除 and 从本教材移除（N）, and the detail drawer's per-meaning 删除整条释义及其全部位置 — goes through one shared dialog (`VocabularyCleanupDialog`) against the cleanup API (`POST /admin/vocabulary-books/{bookId}/cleanup/preview` and `POST /admin/vocabulary-books/{bookId}/cleanup`).

Opening the dialog always fetches a fresh, read-only preview and shows the target book's name, the action, the deduplicated word count, the meaning count, and the estimated orphan word count, stating that these are estimates re-validated against the current content at commit time and that meanings and shared words in other books — disabled ones included — are retained. Deleting a book additionally requires typing its exact name; 确认清理 stays disabled until the typed name matches the previewed one, and the commit sends it as `confirmedBookName`. Cancelling ends the preview only and commits nothing. The confirm button cannot be double-submitted, and closing the dialog never promises to undo a transaction that already started.

A refused preview or commit keeps a distinct message: 404 reports the target gone, 409 explains that the scope or the confirmed name no longer matches the current data and offers 重新预览, 503 reports a busy store, and 400 shows the server reason. A commit whose answer never arrived is an unknown outcome: the dialog warns, asks its surroundings to re-query current state, and never replays itself. The frontend never computes orphan counts or decides deletions from them; only the server's committed result is trusted. On success the pages report the server's actual counts, refresh their lists, and re-read the detail: a deleted book leaves the word list back at 教材管理, a word whose last meaning was deleted closes the drawer, and a page emptied by a removal falls back to the last valid page.

## Book units

教材管理's per-row 单元 opens one shared dialog (`VocabularyBookUnitDialog`) against the unit API (`GET`/`POST`/`PUT`/`DELETE` on `/admin/vocabulary-books/{bookId}/units`). It lists the book's units in unit order with each unit's 关联词义数 — distinct meanings, so a meaning assigned to both Section A and Section B of the unit counts once — and maintains them: the form below the table adds a unit, a row's 编辑 fills the same form for a full replacement (改号 and 改名 together, both fields sent every time with an empty title as `null`), and a row's 删除 first confirms in plain text what it removes — the unit and its assignment count, with meanings, shared words, and other units' assignments retained. Creating a unit is the administrator's explicit act; the import pages never create or rename units. Disabled books are managed the same way, and a book deleted elsewhere is reported with its way back to 教材管理 instead of an empty unit list.

A refused write keeps the entered values: 400 shows the server's reason (a duplicate unit number answers 409 and explains the conflict with a 刷新列表 that re-reads current state), and 404 reports the target gone. A write whose answer never arrived is an unknown outcome: the dialog says so and offers the same re-read, never replaying itself. Success refreshes the unit list. The 关联词义数 shown next to a delete confirmation is a display snapshot the server may have moved past by commit time; the committed result is authoritative. `e2e/book-units.spec.ts` covers the dialog: listing with counts, adding and editing through the strict replace body, the duplicate-number refusal keeping the draft, the delete confirmation and refresh, a failed list load with a working retry, the disabled-book entry, the gone-book notice, and axe plus sideways-scroll checks at 1440 px and 768 px.

## Add one phrase

`/import/phrase` (新增短语) is a single-entry form for an enabled book and one of that book's existing units. The unit is required, Section A or B is optional, and the classification is always `phrase`; spelling and meaning are required, while phonetics, part of speech, and example are optional. The form sends exactly one entry to `POST /admin/vocabulary/batch` with `entryKind:"phrase"` and the selected `unitId`. The service applies its existing normalization, equivalent-meaning reuse, position idempotency, authorization, and atomic write rules. The older `/import` word form is unchanged. Choosing another book clears the previous unit and section, and a late unit response cannot restore a choice from the old book. The form groups book and position, basic information, pronunciation, and meaning/example into cards, with field validation and optional custom parts of speech. Clear content resets entry fields and validation without writing anything, retaining the selected book, unit, and section. Missing units link to book management. A failed unit load can be retried; a refused submission preserves the draft. Submitting freezes a request snapshot and disables all editing, location choices, retries, and clearing until it completes. Leaving the page invalidates its UI responses without undoing a server transaction. A successful response names the submitted phrase and location, distinguishes new from reused meanings, clears entry fields, and retains location choices. `created`/`reused` count meanings, never newly added positions; a reused position may already exist. If no response arrives, the result is unknown; use the phrase-management link to check the current phrase position before retrying; writes are never replayed automatically. Disabled books cannot be selected for creation.

`e2e/phrase-import.spec.ts` covers required/whitespace validation, draft retention, frozen writes, navigation with delayed responses, clear-without-write, precise payloads, and status/unknown-result feedback. The existing `/import` page and the phrase position list both link directly to `/import/phrase`; the old word submission remains unchanged.

## Batch import page

The three batch routes share one parser, preview, and submission flow. `/import/batch` defaults to mixed vocabulary; `/import/batch/words` selects word mode and `/import/batch/phrases` selects phrase mode. The on-page mode switch changes the route without discarding the raw text or workbook. Administrator sessions can open or refresh each deep link with the same mode. The existing hosted-login return allowlist is unchanged: if an unauthenticated administrator starts at a new uniform-mode deep link, signing in returns to the default book page, from which the mode's navigation entry is available; that round trip does not restore the new mode route.

| Mode | File kind | Final kind | Unit resolution |
| --- | --- | --- | --- |
| Mixed | Missing, blank, or JSON null | Unclassified; omit `entryKind` | Use the file unit; blank means no assignment |
| Mixed | `word` / `phrase` | Keep the file value | A valid file unit is required |
| Word / Phrase | Missing, blank, or JSON null | The explicitly selected mode | Use a nonblank file unit first; otherwise use the selected default unit |
| Word / Phrase | The same kind | Keep the file value | Same unit rule |
| Word / Phrase | The opposite kind | Row error; switch to mixed | Never overwrite the file |
| Any | Invalid kind or a non-string JSON value | Row/parser error | Defaults never repair invalid input |

The optional default-unit picker lists only the selected book's existing units, clears on a book change, and is ignored in mixed mode. Uniform-mode rows still need a final unit; unknown nonblank values such as `02` or `Unit 2` remain errors. Sections have no page default and are checked after final unit resolution. All four formats apply this table after their existing strict parsing. Preview columns distinguish the raw file kind/unit from the final kind/unit and mark page defaults. The category summary counts input rows, including duplicates, rather than positions created by the server; `created`/`reused` count meanings. Unclassified mixed rows do not appear in phrase management.

For a pure word CSV choose word mode and default Unit 2: `word,meaning` followed by `apple,苹果`. For a pure phrase CSV choose phrase mode and default Unit 2: `word,meaning` followed by `take off,起飞`. A mixed file instead uses `word,meaning,unit,entry_kind`, with `apple,苹果,2,word` and `take off,起飞,2,phrase`. No mode infers a kind from spaces, spelling, part of speech, or filenames. Reimporting a classified assignment does not move, delete, or reclassify an existing unclassified position.

Changing mode, book, default unit, or input regenerates the preview and clears stale server errors and success notices while preserving the raw input. Submission freezes the final JSON and disables mode/default changes; same-component route navigation cannot change mode during the write. Removing or replacing a file terminates its old Excel worker, and leaving invalidates file/read/write UI results without automatically submitting or undoing a server transaction. `e2e/batch-import-modes.spec.ts` covers the decision table across TSV/CSV/JSON/Excel, preview/payload parity, preserved inputs, stale reads, mode navigation, frozen writes, and full-page accessibility at 1440px/768px.


`/import/batch` imports many entries into one book through `POST /admin/vocabulary/batch`. [ADR-005](../adr/ADR-005-bulk-vocabulary-import.md) is the single source for the payload, the limits, the check order, the failure envelope, and the TSV format, and [ADR-006](../adr/ADR-006-batch-import-file-formats.md) for the other formats, file reading, and the header rules; this section describes only how the page applies them.

The page is laid out as four numbered sections, all visible at once: 1. 选择教材, 2. 输入数据, 3. 预览与校验, and 4. 提交与结果. The input section has a help panel beside the text area when the content area is at least 1000 px wide, and below it otherwise; it describes the selected format (columns or header names, and an example) and, for every format, the accepted extensions, UTF-8, the 1 MiB file limit, the 500-entry batch limit, that a batch is written in one transaction, how the unit column is matched (exactly, against the selected book's numbers, blank for no assignment, never creating a unit), how the section column is matched (`A`/`B` exactly after trimming, blank for no section, requiring the unit column), how the entry-kind column is matched (`word`/`phrase` exactly after trimming with case significant, blank for unclassified, never inferred, requiring the unit column), and that files are parsed only in the browser. The preview section shows an `el-empty` until there is a row to preview, and the submission blockers appear in a warning alert titled 暂时无法提交, directly above the submit button.

Flow:

1. Pick an enabled book. The picker reads `GET /api/vocabulary-books/all`, the same as the single-entry page. Picking a book also reads its units from `GET /admin/vocabulary-books/{bookId}/units`, and so does every change of book; the answer for a book no longer chosen is dropped. While the list cannot be loaded, `.batch-units-error` shows a retryable error and submission is blocked, because the page can neither display nor resolve a unit number.
2. Pick the format, TSV (the default), CSV, or JSON, and paste text into the text area; changing the format parses the same text again. Or choose a local `.tsv`, `.txt`, `.csv`, `.json`, or `.xlsx` file: `.tsv` and `.txt` select TSV, `.csv` selects CSV, `.json` selects JSON, and `.xlsx` selects Excel, ignoring case, and any other extension, `.xls` included, is refused. A file larger than 1 MiB is refused before it is read, so an oversized file cannot stall the preview. The file is read in the browser with `file.arrayBuffer()` and decoded with `new TextDecoder('utf-8', { fatal: true })`, which removes a leading byte-order mark; a file that is not UTF-8 is refused with a message asking for it to be saved as UTF-8, and the text area and the format are left as they were. A file that decodes is placed in the text area and switches the selector to its format; it is never uploaded. An `.xlsx` file is read in a worker instead: the selector shows Excel and, like the text area, is disabled, the text area shows the file name, and **移除文件** (`.batch-remove-file`) returns to an empty TSV text area. A workbook is also cleared by choosing another file and by a successful import.
3. The preview counts data rows, valid rows, and invalid rows, and lists each row with its position (the source line number it starts on, the sheet row number for Excel, or for JSON the item's number, in a column headed 序号 instead of 行号), its nine columns in the canonical order, and its status. The unit column shows the resolved unit's number and title (`2 · School Life`, or just `2` when the unit has no title), the raw value when it matches no unit, and nothing when the row carries no assignment; the section and entry-kind columns show the raw values as written. The table shows 100 rows per page and can be filtered to invalid rows only. When the input cannot be read as a whole — for example a CSV header that is unknown, duplicated, or missing `word` or `meaning`, an unclosed quote, a JSON syntax error, a JSON top level that is not an array, or a workbook that cannot be read or takes longer than 15 seconds — the page shows that file-level error in `.batch-parse-error` instead of the preview, and the submit checks list only that error, plus the missing book if there is none.
4. Submitting sends the parsed JSON, never the file. The request uses the same cookie session and `X-Requested-With` header as every other administration write, and the button stays disabled while the request is in flight, so repeated clicks send one request.
5. On success the page shows `total`, `created`, and `reused`, clears the text and the preview so the same batch is not sent twice by accident, and keeps the selected book.

Browser-side TSV rules, in addition to the format ADR-005 defines:

- Line numbers are physical line numbers starting at 1. Blank lines and comment lines are skipped but still counted.
- `\r\n`, `\n`, and `\r` all end a line, and a leading UTF-8 byte-order mark is removed.
- A line is skipped when it is blank after trimming, or when its first character is `#`. Leading whitespace before `#` makes the line a data line.
- Every column is trimmed. A blank optional column is left out of the entry rather than sent as an empty string. A line whose `word` or `meaning` is blank, or that does not have exactly five through nine columns, is invalid.
- `entries[i]` of the request is the `i`th data line of the preview, in source order, so the server's `errors[].index` maps back to a source line.

Browser-side CSV rules, as ADR-006 defines them:

- The first record that is not blank is a header naming the columns: `word`, `phonetic_uk`, `phonetic_us`, `part_of_speech`, `meaning`, `example`, `unit`, `section`, and `entry_kind`, trimmed, in any order and any case. `word` and `meaning` are required. A column with a blank header is ignored when every value under it is blank; an unknown, duplicated, or missing required name, or a blank name over values, refuses the whole input with the column number and name.
- Only a comma separates fields. A field whose first character is `"` is quoted: inside it, commas and line breaks are content, `""` is one quote, and every line break becomes `\n`. A quote left open, or a closing quote followed by anything but a comma or a line break, refuses the whole input.
- A row's position is the physical line its record starts on, so a line break inside a quoted field moves the later rows down. Records whose fields are all blank are skipped but counted; a record starting with `#` is data.
- A record with a different number of fields from the header is invalid. Values are trimmed and blank optional values are left out, with the same check as TSV, so the same data gives the same request in either format.

Browser-side JSON rules, as ADR-006 defines them:

- The input is an array of entry objects with the API field names `word`, `phoneticUk`, `phoneticUs`, `partOfSpeech`, `meaning`, `example`, `unit`, `section`, and `entryKind`, the `entries` of the request without `bookId`. A leading byte-order mark is removed, and blank text is no input yet.
- A syntax error, shown with the browser's message and its position, or a top level that is not an array refuses the whole input. A `{ bookId, entries }` object is refused with a note to provide only the `entries` array, because the book is the one picked on the page.
- An item's position is its number in the array, from 1. An item is invalid when it is not an object, when it has any other key (including `__proto__`), or when a value is neither a string nor `null`; such a value is shown in the preview as JSON and is never converted. An absent or `null` field is blank.
- String values are trimmed and blank optional values are left out, with the same check as TSV, so the same data gives the same request in either format. A key repeated within one object keeps its last value and is not reported.

Browser-side Excel rules, as ADR-006 defines them:

- Only `.xlsx` is read, in a dedicated worker that is terminated once it answers, after 15 seconds, or when the file is removed or replaced. Before anything is unzipped, a workbook whose entries declare more than 32 MiB in total, or whose declared sizes are forged, is refused.
- Only the first sheet is read; with several sheets, `.batch-sheet-notice` says how many there are and names the one read. A row's position is its sheet row number, and blank rows are skipped but counted.
- The first row that is not blank is the header, with the same rules as CSV. Cells beyond the header are ignored only when their whole column is blank.
- Text is read as it is and a formula as its cached result; numbers become `String(value)` without their number format, booleans `TRUE` or `FALSE`, and dates `YYYY-MM-DD`, or `YYYY-MM-DDTHH:mm:ss` with a time, in UTC. Empty cells, merged cells other than the top-left one, and error values are blank. Values are then trimmed and checked as for TSV, so the same sheet gives the same request.

The unit column, as ADR-006 defines it, is shared by every format: the TSV seventh column, the CSV and Excel `unit` header column, and the JSON `unit` field.

- The value is a unit number as the unit administration page shows it. It is trimmed and compared exactly with each unit's number written as a string, so a padded `02` or a spelling like `Unit 2` matches nothing; a wrong unit must stop the batch rather than guess.
- A blank value is no assignment, and an input without the unit column or field at all parses and submits exactly as before: its entries carry no `unitId`.
- A value that matches no unit of the selected book makes its row invalid with the value shown (`未知单元：02`), counted in the invalid total and blocking submission. Changing the book, the text, the format, or the workbook re-resolves every row against the newly selected book's units.
- A resolved assignment is sent as the entry's `unitId`; the server re-validates it, so a unit deleted between the list load and the submit comes back as that row's server reason.

The section column, as ADR-006 defines it, is shared by every format too: the TSV eighth column, the CSV and Excel `section` header column, and the JSON `section` field.

- The value is `A` or `B` exactly, after trimming and with case significant: ` A ` is Section A, while `a` matches nothing and makes the row invalid with the value shown (`分节应为 A 或 B：a`); a wrong section must stop the batch rather than guess.
- A blank value is no section, and an input without the section column or field at all parses and submits exactly as before: its entries carry no `section`.
- A section requires the final unit, including a page default in uniform mode: a section without a unit number makes the row invalid (`有分节但未填写单元`), because it names a place of nothing.
- A resolved section is sent as the entry's `section` only when the row carries one; the server re-validates it.

The entry-kind column, as ADR-006 defines it, is shared by every format too: the TSV ninth column, the CSV and Excel `entry_kind` header column, and the JSON `entryKind` field.

- The value is `word` or `phrase` exactly, after trimming and with case significant: ` word ` is a word, while `Word` matches nothing and makes the row invalid with the value shown (`类别应为 word 或 phrase：Word`); a wrong kind must stop the batch rather than guess. Nothing about the entry's text is examined to infer a kind.
- In mixed mode, a blank value is unclassified, and an input without the kind column or field at all parses and submits exactly as before: its entries carry no `entryKind`.
- A kind requires the final unit, because it is a property of an assignment's place: a kind without a unit number makes the row invalid (`有类别但未填写单元`), because it classifies nothing.
- A resolved kind is sent as the entry's `entryKind` when the file or explicit uniform mode supplies one; the server re-validates it.

Submission is disabled, with a message saying why, when no book is selected, the selected book's unit list is still loading or has failed to load, there is no data line, any line is invalid, there are more than 500 data lines, or the JSON payload is larger than 1,048,576 bytes in UTF-8. These checks only spare a request the server would refuse; the server validates every entry itself. The page does not split a large input into several batches: each batch is atomic, but several batches together are not, and splitting them automatically would suggest otherwise.

Results and failures:

| Answer | Page behaviour |
|------|----------|
| 200 | Show total, created, and reused |
| 400 with `errors` | Show each server reason on the source line of its `index`, filter the preview to invalid lines, and state that the batch was not written |
| 400 without `errors`, or with an `index` outside the batch | Show the server message |
| 401 / 403 | Redirect to the login or forbidden page |
| 404 | The selected book does not exist; refresh and pick again |
| 409 | The word or meaning conflicts with existing data |
| 413 | The request body is over 1 MiB; split the batch |
| 422 | The selected book is disabled |
| 503 | The service is busy and the batch was not written; retry later |
| No response (network error or timeout) | The outcome is unknown; resubmitting the same batch is safe |
| Any other status | Show the server message |

Every failure other than 401 and 403 keeps the text and the preview so the batch can be corrected and resubmitted. Changing the format, the text, or the book clears the server's per-line reasons, because they described the batch that was sent. Unsubmitted text is not saved and is lost when the page is left or reloaded.

`e2e/batch-import.spec.ts`, `e2e/batch-import-json.spec.ts`, `e2e/batch-import-formats.spec.ts`, and `e2e/batch-import-excel.spec.ts` cover the page format by format — the TSV preview and payload, the per-format parity of the same batch, file-level refusals, server entry errors mapped to their source lines, and the Excel worker's bounds — and `e2e/batch-import-units.spec.ts` covers the unit, section, and entry-kind columns for every format: two different units resolving into their `unitId`s with unit-less rows carrying no field, a non-string JSON `unit`, `section`, or `entryKind` refused, unknown (`02`, `Unit 2`, out-of-range) and blank values, sections resolving into `A`/`B` with a padded value trimmed and a lowercase one refused, entry kinds resolving into `word`/`phrase` with a padded value trimmed and the capitalized `Word` refused, a section or a kind without a unit refused on its row, the old shapes (eight-column TSV, header sets without `entry_kind`, JSON without `entryKind`) submitting exactly as before, the re-resolution on a book switch, a server unit rejection landing on its source line, a failed unit-list load with a working retry that unblocks submission, and the re-check when a file replaces pasted text; `e2e/batch-import-excel.spec.ts` covers the workbook `section` and `entry_kind` header columns the same way.

## Layout and visual conventions

### Shell

Once signed in, every page sits in one shell:

- A 56 px header across the page holds `.brand` (`Lexarbor`) on the left, the low-key build-version button beside it, and `.session` (the username and the **退出登录** button) on the right.
- Below it, a side navigation `<nav aria-label="主导航">` is grouped by task. Each group is a `role="group"` labelled by its title, and each entry is a real link (`RouterLink`), not a menu item:
  - **教材**: 教材管理 (`/books`), 单词管理 (`/vocabulary`), and 短语管理 (`/phrases`).
  - **词汇导入**: 单条导入 (`/import`), 新增短语 (`/import/phrase`), 批量导入 (`/import/batch`), 批量单词导入 (`/import/batch/words`), and 批量短语导入 (`/import/batch/phrases`).
- A side navigation rather than header links, because the groups need to be visible and later pages need vertical room; three links in the header can show neither.
- The current page's link is highlighted and carries `aria-current="page"`, for an exact route match only.
- At 1024 px and wider the navigation is 220 px wide with icons and text. From 768 px to 1023 px it collapses to a 64 px icon bar: each link keeps its name through `aria-label` and a `title` tooltip, and the group titles are hidden visually but not from assistive technology. At 768 px no page scrolls sideways.
- The content area is at most 1200 px wide, with 24 px padding on wide screens and 16 px below 1024 px.
- Keyboard order follows the DOM: the logout button, then the build-version button (which CSS order places beside the brand), then the navigation links, then the page. Our own links and buttons show `outline: 2px solid var(--lx-color-focus)` on `:focus-visible`; Element Plus components keep their own focus style.
- The login and forbidden pages have no shell. They use the same brand mark, card, and tokens.

### Tokens

`src/styles/tokens.scss` is the only file under `src/` that may contain a colour literal (`#rgb`, `#rrggbb`, or `rgb(`). Everything else reads the custom properties it defines on `:root`:

| Group | Properties |
|------|----------|
| Colour | `--lx-color-primary`, `-primary-hover`, `-primary-soft`, `-success`, `-warning`, `-danger`, `-info`, `-on-primary`; `--lx-color-text-{primary,regular,secondary,placeholder}`; `--lx-color-border`, `-border-light`; `--lx-color-bg-page`, `-bg-surface`; `--lx-color-focus` |
| Type | `--lx-font-size-{xs,sm,md,lg,xl}`: 12 / 14 / 16 / 20 / 24 px for caption, body, section title, card title, and the page `h1` |
| Spacing | `--lx-space-1` to `--lx-space-6`: 4 / 8 / 12 / 16 / 24 / 32 px |
| Shape | `--lx-radius-sm` (4 px), `--lx-radius-md` (8 px), `--lx-shadow-1` |
| Density | `--lx-density-control-height` (32 px), `--lx-density-table-row-height` (40 px) |

The same Sass source values override Element Plus's variables: `--el-color-{primary,success,warning,danger,error,info}` with their `-light-3/5/7/8/9` and `-dark-2` shades (mixed with `sass:color`), `--el-text-color-*`, `--el-border-color*`, `--el-bg-color-page`, `--el-border-radius-*`, and `--el-font-size-*`. Element Plus components and our own styles therefore share one palette.

Contrast: body text, links, button text, and tag text reach at least 4.5:1 against their background, including while hovered and pressed. Element Plus lightens a filled button (`light-3`) and a link button (`light-5`) on hover, which falls to between 2.15:1 and 3.43:1 with this palette, so `tokens.scss` darkens both to `dark-2` instead. The file's header comment lists the computed ratio of every foreground and background pair; update it with any colour change.

Element Plus uses its Chinese locale (`element-plus/es/locale/lang/zh-cn`), so the pagination reads 共 N 条 / 前往 and confirmation boxes offer 确定 / 取消.

### Page structure

Each page starts with `PageHeader` (`src/components/PageHeader.vue`):

```vue
<PageHeader title="教材管理" description="维护教材信息，启用或停用教材">
  <template #actions>
    <el-button type="primary">新增教材</el-button>
  </template>
</PageHeader>
```

It renders the page's only `h1`, an optional description, and an optional `actions` slot on the right, which moves below the title below 1024 px.

Loading, empty, and failed states look the same on every page:

- Loading: `v-loading` on the region being loaded, not on the whole page.
- Empty: `el-empty` whose description says what there is none of.
- Failed to load: keep the `ElMessage` error, and show an `el-alert type="error"` with a **重试** button inside the region.

### Accessibility tests

`e2e/vocabulary.spec.ts` covers the two list pages and the shared detail drawer described above: entering a book's list from 查看单词 with its identity, status, and the two whole-book counts (a word with two meanings stays one row and one count), server-side keyword and paging parameters with counts that do not shrink, the empty-book versus keyword-miss distinction, maintaining a disabled book and finding the way back, a missing book and a failed load with a retry, the whole-library list with disabled-only and unassigned words, the book filter's remote search and its paging past the first page with a disabled option, the drawer's grouped read-only detail from both lists with Escape closing and focus returning to its button, the drawer's failure/404/unassigned states, out-of-order answers for both a replaced word and a replaced book (including a same-document book switch and an answer that lands after leaving the page), a 401 keeping the shared redirect, and axe plus sideways-scroll checks at 1440 px and 768 px for both pages and the open drawer. `e2e/book-words-units.spec.ts` covers the book words page's unit browsing: the whole-book → Unit 2 → Unit 6 → whole-book switches each served by its own read with the counts and the meanings column switching scope, one meaning shown in both of its units while another word's two meanings split between them, the unit view's server-side keyword and paging, the section picker narrowing the unit view to Section A, Section B, or the unsectioned places with the counts following and the whole-unit section counts staying, the entry-kind picker narrowing the same view to 单词, 短语, or the unclassified places the same way with the whole-unit kind counts staying and the two pickers combining by intersection, a section or kind reset by a unit switch, the drawer's tags naming each place's section and kind, the empty unit, the disabled book's unit view, a late unit answer ignored after the view moved on, a unit deleted while open answering 404 with the way back to the whole book, a failed unit read retried, the unit list's own failure and retry keeping the whole-book view usable, both whole-book removal entries disabled in a unit view with the explaining tooltip and re-enabled in the whole book, the drawer's unit tags on a meaning, the unit picker reached from the keyboard, and axe plus sideways-scroll checks at 1440 px and 768 px for the unit view. `e2e/layout.spec.ts` runs axe (`@axe-core/playwright`, tags `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`) at 1440 px and 768 px over the whole login and forbidden pages, over the header and navigation of `/books`, `/vocabulary`, `/import`, and `/import/batch`, and over the whole of `/books` (with books, with none, and with the new book dialog open), `/import` (on arrival and after a successful import), and `/import/batch` (when empty, with valid and invalid rows in the preview, and with a file-level error); each must report no violation. A page body is added to the full-page check when that page is redesigned. The same specification checks sideways scrolling at 768 px (including `/import/batch` with a preview), the batch import help following the selected format and sitting beside the text area at 1440 px and below it at 768 px, the batch import preview's empty state, navigation and `aria-current`, the keyboard order (the logout button, the build-version button, then the links) and focus ring, logout including a failed logout, and the Chinese locale. `e2e/version.spec.ts` covers the build-version display described above: every channel's label and full-identity dialog, keyboard and pointer opening with Escape and outside-click closing, one request per session generation with no route-change refetch and a reload refetch, the login-then-restore deduplication, no request on the guest pages, every failure mode reading 版本未知 with the page still working and no retry, a current 401/403 keeping its redirect, the delayed late-answer races across a logout and a same-username re-login, empty web storage, and the 375 px header with a long prerelease and a long username.
Two conventions follow from these checks. `/books` sets the page size with its own select labelled 每页条数, to the left of the pagination, rather than with the pagination's built-in size picker, whose input cannot be given an accessible name. A table whose columns can overflow at 768 px sets `scrollbar-tabindex="0"`, so its horizontal scroll region can be reached from the keyboard.

## Build

```bash
npm ci
npm run test:types
npm run build
```

Vite writes to the frontend's own `dist/`, and the root Dockerfile copies that directory into the .NET Host's `wwwroot` publish content during the image build.
