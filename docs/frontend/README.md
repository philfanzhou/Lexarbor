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
| `/login` | Anonymous | Identity administrator username and password login |
| `/forbidden` | Anonymous | Non-administrator notice |
| `/books` | Administrator | Vocabulary book management |
| `/import` | Administrator | Word import |
| `/import/batch` | Administrator | Batch word import |

The application uses hash history. On first entry to a protected page it calls `GET /admin/auth/session` to restore the cookie session; an unauthenticated response redirects to `/login` and a 403 redirects to `/forbidden`. While unauthenticated, neither the administration navigation nor any actionable page is rendered.

## Authentication API

| Method and path | Request and response |
|------------|-----------|
| `POST /admin/auth/login` | `{ username, password }`; on success returns non-sensitive session information only |
| `GET /admin/auth/session` | Returns the current administrator session |
| `POST /admin/auth/logout` | Deletes the server-side cookie; the frontend always clears its local state |

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
login(username, password)
restoreSession()
logout()
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

## Existing pages

- Book management keeps search, paging, create, edit, status toggle, and delete.
- The administration list contains both enabled and disabled books.
- Deleting a book that still has meanings answers 409, which prompts the administrator to disable it.
- Word import keeps the book, word, British phonetic, American phonetic, part of speech, definition, and example sentence fields.
- The import page's book picker reads `GET /api/vocabulary-books/all`, which is unpaged and enabled-only. The paged administration search is the wrong source for a picker: called with no paging parameters it answers 400, and called with them it answers one page, so a deployment with more than one page of books would silently lose the rest. The administration list also offers disabled books, which the import endpoint then refuses with a 422.
- Both existing features must remain usable after a successful login.

## Batch import page

`/import/batch` imports many entries into one book through `POST /admin/vocabulary/batch`. [ADR-005](../adr/ADR-005-bulk-vocabulary-import.md) is the single source for the payload, the limits, the check order, the failure envelope, and the TSV format, and [ADR-006](../adr/ADR-006-batch-import-file-formats.md) for the other formats, file reading, and the header rules; this section describes only how the page applies them.

Flow:

1. Pick an enabled book. The picker reads `GET /api/vocabulary-books/all`, the same as the single-entry page.
2. Pick the format, TSV (the default), CSV, or JSON, and paste text into the text area; changing the format parses the same text again. Or choose a local `.tsv`, `.txt`, `.csv`, `.json`, or `.xlsx` file: `.tsv` and `.txt` select TSV, `.csv` selects CSV, `.json` selects JSON, and `.xlsx` selects Excel, ignoring case, and any other extension, `.xls` included, is refused. A file larger than 1 MiB is refused before it is read, so an oversized file cannot stall the preview. The file is read in the browser with `file.arrayBuffer()` and decoded with `new TextDecoder('utf-8', { fatal: true })`, which removes a leading byte-order mark; a file that is not UTF-8 is refused with a message asking for it to be saved as UTF-8, and the text area and the format are left as they were. A file that decodes is placed in the text area and switches the selector to its format; it is never uploaded. An `.xlsx` file is read in a worker instead: the selector shows Excel and, like the text area, is disabled, the text area shows the file name, and **移除文件** (`.batch-remove-file`) returns to an empty TSV text area. A workbook is also cleared by choosing another file and by a successful import.
3. The preview counts data rows, valid rows, and invalid rows, and lists each row with its position (the source line number it starts on, the sheet row number for Excel, or for JSON the item's number, in a column headed 序号 instead of 行号), its six columns in the canonical order, and its status. The table shows 100 rows per page and can be filtered to invalid rows only. When the input cannot be read as a whole — for example a CSV header that is unknown, duplicated, or missing `word` or `meaning`, an unclosed quote, a JSON syntax error, a JSON top level that is not an array, or a workbook that cannot be read or takes longer than 15 seconds — the page shows that file-level error in `.batch-parse-error` instead of the preview, and the submit checks list only that error, plus the missing book if there is none.
4. Submitting sends the parsed JSON, never the file. The request uses the same cookie session and `X-Requested-With` header as every other administration write, and the button stays disabled while the request is in flight, so repeated clicks send one request.
5. On success the page shows `total`, `created`, and `reused`, clears the text and the preview so the same batch is not sent twice by accident, and keeps the selected book.

Browser-side TSV rules, in addition to the format ADR-005 defines:

- Line numbers are physical line numbers starting at 1. Blank lines and comment lines are skipped but still counted.
- `\r\n`, `\n`, and `\r` all end a line, and a leading UTF-8 byte-order mark is removed.
- A line is skipped when it is blank after trimming, or when its first character is `#`. Leading whitespace before `#` makes the line a data line.
- Every column is trimmed. A blank optional column is left out of the entry rather than sent as an empty string. A line whose `word` or `meaning` is blank, or that does not have exactly five or six columns, is invalid.
- `entries[i]` of the request is the `i`th data line of the preview, in source order, so the server's `errors[].index` maps back to a source line.

Browser-side CSV rules, as ADR-006 defines them:

- The first record that is not blank is a header naming the columns: `word`, `phonetic_uk`, `phonetic_us`, `part_of_speech`, `meaning`, and `example`, trimmed, in any order and any case. `word` and `meaning` are required. A column with a blank header is ignored when every value under it is blank; an unknown, duplicated, or missing required name, or a blank name over values, refuses the whole input with the column number and name.
- Only a comma separates fields. A field whose first character is `"` is quoted: inside it, commas and line breaks are content, `""` is one quote, and every line break becomes `\n`. A quote left open, or a closing quote followed by anything but a comma or a line break, refuses the whole input.
- A row's position is the physical line its record starts on, so a line break inside a quoted field moves the later rows down. Records whose fields are all blank are skipped but counted; a record starting with `#` is data.
- A record with a different number of fields from the header is invalid. Values are trimmed and blank optional values are left out, with the same check as TSV, so the same data gives the same request in either format.

Browser-side JSON rules, as ADR-006 defines them:

- The input is an array of entry objects with the API field names `word`, `phoneticUk`, `phoneticUs`, `partOfSpeech`, `meaning`, and `example`, the `entries` of the request without `bookId`. A leading byte-order mark is removed, and blank text is no input yet.
- A syntax error, shown with the browser's message and its position, or a top level that is not an array refuses the whole input. A `{ bookId, entries }` object is refused with a note to provide only the `entries` array, because the book is the one picked on the page.
- An item's position is its number in the array, from 1. An item is invalid when it is not an object, when it has any other key (including `__proto__`), or when a value is neither a string nor `null`; such a value is shown in the preview as JSON and is never converted. An absent or `null` field is blank.
- String values are trimmed and blank optional values are left out, with the same check as TSV, so the same data gives the same request in either format. A key repeated within one object keeps its last value and is not reported.

Browser-side Excel rules, as ADR-006 defines them:

- Only `.xlsx` is read, in a dedicated worker that is terminated once it answers, after 15 seconds, or when the file is removed or replaced. Before anything is unzipped, a workbook whose entries declare more than 32 MiB in total, or whose declared sizes are forged, is refused.
- Only the first sheet is read; with several sheets, `.batch-sheet-notice` says how many there are and names the one read. A row's position is its sheet row number, and blank rows are skipped but counted.
- The first row that is not blank is the header, with the same rules as CSV. Cells beyond the header are ignored only when their whole column is blank.
- Text is read as it is and a formula as its cached result; numbers become `String(value)` without their number format, booleans `TRUE` or `FALSE`, and dates `YYYY-MM-DD`, or `YYYY-MM-DDTHH:mm:ss` with a time, in UTC. Empty cells, merged cells other than the top-left one, and error values are blank. Values are then trimmed and checked as for TSV, so the same data gives the same request.

Submission is disabled, with a message saying why, when no book is selected, there is no data line, any line is invalid, there are more than 500 data lines, or the JSON payload is larger than 1,048,576 bytes in UTF-8. These checks only spare a request the server would refuse; the server validates every entry itself. The page does not split a large input into several batches: each batch is atomic, but several batches together are not, and splitting them automatically would suggest otherwise.

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

## Layout and visual conventions

### Shell

Once signed in, every page sits in one shell:

- A 56 px header across the page holds `.brand` (`Lexarbor`) on the left and `.session` (the username and the **退出登录** button) on the right.
- Below it, a side navigation `<nav aria-label="主导航">` is grouped by task. Each group is a `role="group"` labelled by its title, and each entry is a real link (`RouterLink`), not a menu item:
  - **教材**: 教材管理 (`/books`). A later book word list page belongs to this group.
  - **词汇导入**: 单条导入 (`/import`) and 批量导入 (`/import/batch`).
- A side navigation rather than header links, because the groups need to be visible and later pages need vertical room; three links in the header can show neither.
- The current page's link is highlighted and carries `aria-current="page"`, for an exact route match only.
- At 1024 px and wider the navigation is 220 px wide with icons and text. From 768 px to 1023 px it collapses to a 64 px icon bar: each link keeps its name through `aria-label` and a `title` tooltip, and the group titles are hidden visually but not from assistive technology. At 768 px no page scrolls sideways.
- The content area is at most 1200 px wide, with 24 px padding on wide screens and 16 px below 1024 px.
- Keyboard order follows the DOM: the logout button, then the navigation links, then the page. Our own links and buttons show `outline: 2px solid var(--lx-color-focus)` on `:focus-visible`; Element Plus components keep their own focus style.
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

`e2e/layout.spec.ts` runs axe (`@axe-core/playwright`, tags `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa`) at 1440 px and 768 px over the whole login and forbidden pages, over the header and navigation of `/books`, `/import`, and `/import/batch`, and over the whole of `/books` (with books, with none, and with the new book dialog open) and `/import` (on arrival and after a successful import); each must report no violation. A page body is added to the full-page check when that page is redesigned. The same specification checks sideways scrolling at 768 px, navigation and `aria-current`, the keyboard order and focus ring, logout including a failed logout, and the Chinese locale.

Two conventions follow from these checks. `/books` sets the page size with its own select labelled 每页条数, to the left of the pagination, rather than with the pagination's built-in size picker, whose input cannot be given an accessible name. A table whose columns can overflow at 768 px sets `scrollbar-tabindex="0"`, so its horizontal scroll region can be reached from the keyboard.

## Build

```bash
npm ci
npm run test:types
npm run build
```

Vite writes to the frontend's own `dist/`, and the root Dockerfile copies that directory into the .NET Host's `wwwroot` publish content during the image build.
