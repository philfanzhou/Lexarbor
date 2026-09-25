# Lexarbor administration frontend specification

## Stack and boundaries

- Vue 3.5, TypeScript, Vue Router, Element Plus, Axios, Vite.
- No additional state management or authentication dependency.
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
2. Pick the format, TSV (the default) or CSV, and paste text into the text area; changing the format parses the same text again. Or choose a local `.tsv`, `.txt`, or `.csv` file: `.tsv` and `.txt` select TSV and `.csv` selects CSV, ignoring case, and any other extension is refused. A file larger than 1 MiB is refused before it is read, so an oversized file cannot stall the preview. The file is read in the browser with `file.arrayBuffer()` and decoded with `new TextDecoder('utf-8', { fatal: true })`, which removes a leading byte-order mark; a file that is not UTF-8 is refused with a message asking for it to be saved as UTF-8, and the text area and the format are left as they were. A file that decodes is placed in the text area and switches the selector to its format; it is never uploaded.
3. The preview counts data rows, valid rows, and invalid rows, and lists each row with its position (the source line number it starts on), its six columns in the canonical order, and its status. The table shows 100 rows per page and can be filtered to invalid rows only. When the input cannot be read as a whole — for example a CSV header that is unknown, duplicated, or missing `word` or `meaning`, or an unclosed quote — the page shows that file-level error in `.batch-parse-error` instead of the preview, and the submit checks list only that error, plus the missing book if there is none.
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

## Build

```bash
npm ci
npm run test:types
npm run build
```

Vite writes to the frontend's own `dist/`, and the root Dockerfile copies that directory into the .NET Host's `wwwroot` publish content during the image build.
