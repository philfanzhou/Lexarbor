# ADR-006 Batch import file formats

- **Status**: accepted; implemented
- **Date**: 2026-09-25
- **Scope**: The input formats the administration UI's `/import/batch` page accepts, how it reads and decodes a local file, and the rules every format shares. The `POST /admin/vocabulary/batch` contract of [ADR-005](./ADR-005-bulk-vocabulary-import.md), the server, the SQLite schema, and the single-entry `/import` page are unchanged

## Context

[ADR-005](./ADR-005-bulk-vocabulary-import.md) added a batch import whose page parses TSV in the browser and submits JSON. Most word lists administrators hold are spreadsheet exports: comma-separated with a header row, and, from Chinese-language Windows, often not UTF-8. The page read files with `FileReader.readAsText(file, 'utf-8')`, which replaces every byte sequence that is not UTF-8 with U+FFFD; the preview then showed garbled text that was still valid and could be submitted, and stored as it was. [Issue #78](https://github.com/philfanzhou/Lexarbor/issues/78) tracks CSV, Excel, and JSON support; [Issue #79](https://github.com/philfanzhou/Lexarbor/issues/79) holds the slice this decision was written for: the shared reading layer, the header rules, and CSV. [Issue #80](https://github.com/philfanzhou/Lexarbor/issues/80) added JSON.

## Decision

### Every format produces the same preview rows

Each format has a parser that turns text into either a list of preview rows or one file-level error, never both:

- A **preview row** has a position, the six values in the canonical order `word`, `phonetic_uk`, `phonetic_us`, `part_of_speech`, `meaning`, `example`, and either an entry or a reason it is invalid.
- A **file-level error** means the input as a whole cannot be read. The page shows the error in place of the preview, renders no rows, and refuses to submit.

Every format checks its rows with the same function: values are trimmed, a row with a blank `word` or `meaning` is invalid, and a blank optional value is left out of the entry rather than sent as an empty string. The same data therefore produces a byte-identical request whichever format it was imported from. `entries[i]` of the request is the `i`th preview row, so the server's `errors[].index` maps back to a position. The preview, the invalid-row filter, paging, the submit checks, and the mapping of server errors do not depend on the format.

What a position means depends on the format:

| Format | Position |
|---|---|
| TSV | The physical line of the row, from 1 |
| CSV | The physical line the record starts on, from 1 |
| JSON | The item's number in the array, from 1 |

### Choosing the format

The page has a format selector with TSV, CSV, and JSON; TSV is the default. Pasted text is parsed in the selected format, and changing the format parses the same text again. Choosing a file selects its format by extension, ignoring case:

| Extension | Format |
|---|---|
| `.tsv`, `.txt` | TSV |
| `.csv` | CSV |
| `.json` | JSON |

Any other extension is refused. A file is checked in this order, and the first check that fails decides: extension, then size (1 MiB, the ADR-005 body limit), then content. The first two are decided before the file is read. A file that passes is placed in the text area and the selector switches to its format.

Changing the format, the text, or the book, including by choosing a file, clears any per-row reasons from the server, because they described the batch that was sent.

### Files must be UTF-8

A file is read as bytes and decoded with `new TextDecoder('utf-8', { fatal: true })`. A leading UTF-8 byte-order mark is removed; the parsers also remove one from pasted text. A byte sequence that is not UTF-8 refuses the whole file with a message asking for it to be saved as UTF-8, and leaves the text area and the selector as they were. This applies to TSV files as well: a non-UTF-8 TSV file that used to produce a garbled but valid preview is now refused. Replacing the invalid bytes would put U+FFFD into entries that look valid, and a garbled word stored in a shared word row is hard to find afterwards; refusing is the only answer that keeps the administrator from submitting it.

Encodings are not detected. Text in another encoding that happens to be valid UTF-8 is shown as UTF-8, which is rare for real text and visible in the preview.

### Header rules

Formats with a header row, CSV now and Excel later, share these rules:

- The names are the ADR-005 TSV column names: `word`, `phonetic_uk`, `phonetic_us`, `part_of_speech`, `meaning`, `example`. They are trimmed and matched without regard to case, and the columns may be in any order.
- `word` and `meaning` are required; the other columns may be absent, and an absent column is blank in every row.
- A column whose name is blank is ignored when every value under it is blank after trimming, such as the trailing empty column spreadsheet exports often write.
- Each of the following is a file-level error that names the column number and, where there is one, the name: an unrecognized name, a name used twice, a blank name over a column with values, and a missing `word` or `meaning` column.
- There are no aliases, Chinese or otherwise.

A header therefore never drops a column silently: a column is either mapped, or blank throughout, or the whole input is refused.

### CSV

CSV follows RFC 4180 with a comma separator and a header row:

- `\r\n`, `\n`, and `\r` all end a record, and the last record may have no line break.
- A field is quoted only when its first character is `"`. Inside a quoted field, commas and line breaks are content and `""` is one `"`; every line break inside it is stored as `\n`, so a file and the same text pasted into the text area, which normalizes line breaks, give the same values. In an unquoted field `"` is an ordinary character.
- A closing quote followed by anything other than a comma, a line break, or the end of the input is a file-level error naming the line. A quote still open at the end of the input is a file-level error naming the line the field started on.
- A record whose fields are all blank after trimming, such as `,,,,`, is skipped, but its lines still count. CSV has no comments: a record starting with `#` is data.
- The first record that is not skipped is the header. When no record follows it, there is nothing to import.
- A data record with a different number of fields from the header is an invalid row.
- The parser is hand-written and makes one linear pass, so a 1 MiB input parses synchronously on the main thread, as TSV does.

### JSON

JSON is an array of entries that use the field names of the API: the items of the ADR-005 `entries` array, without the `bookId` around them.

- The text is parsed with `JSON.parse` after a leading byte-order mark is removed. Blank text is no input yet: it gives no rows and no error. A syntax error is a file-level error, `JSON 语法错误：` followed by the browser's own message, shown as it is; Chromium's message names the position, for example `at position 14 (line 1 column 15)`.
- The top level must be an array; anything else is a file-level error. When it is an object with an `entries` property, such as a complete `{ bookId, entries }` request, the error adds that only the array is accepted and that the book is the one picked on the page.
- Each item is one preview row, numbered from 1 in array order. The preview heads its position column 序号 (number) for JSON instead of 行号 (line).
- An item that is not an object, including an array and `null`, is invalid.
- The fields are `word`, `phoneticUk`, `phoneticUs`, `partOfSpeech`, `meaning`, and `example`, matched with their case. The keys are read with `Object.keys`, and any other key makes the item invalid with every such key named. That includes `__proto__`, which `JSON.parse` creates as an ordinary own key. There are no aliases such as `phonetic_uk`.
- Every value must be a string. A field that is absent or `null` is blank: a blank optional field is left out, and a blank `word` or `meaning` is reported by the shared row check. A number, boolean, object, or array makes the item invalid rather than being converted, and the preview shows it as `JSON.stringify` writes it, so the administrator sees the value that was refused.
- String values are then trimmed and checked like every other format's rows, so an item gives the same entry as a TSV line with the same values. When an item has several problems, their reasons are joined with `；`, as for TSV.
- An empty array has no data rows.

An item with an unknown field or a value of the wrong type is therefore never corrected or cut down: it is invalid, and nothing can be submitted until it is fixed. A syntax error or a top level that is not an array gives no preview at all.

Not guaranteed: a key repeated within one object is not detected, because `JSON.parse` keeps its last value; and how precisely a syntax error is located depends on the browser's message.

### Excel

Excel `.xlsx` ([Issue #81](https://github.com/philfanzhou/Lexarbor/issues/81)) will add its own section, extension, and position definition to this decision when it is implemented.

### User-supplied data

Imported entries remain user-supplied data under [ADR-002](./ADR-002-bundled-vocabulary-data.md) and [ADR-005](./ADR-005-bulk-vocabulary-import.md). Every format is parsed in the browser; the file never leaves it, and the server receives the same JSON as for TSV. Administrators are responsible for saving their files as UTF-8, CSV files comma-separated with a header row, and JSON files as an array that uses the API field names, and for having the rights to what they import.

## Alternatives considered

- **Parse files on the server**: rejected for the reason ADR-005 gives; it adds an upload surface for formats the browser can turn into JSON.
- **CSV or Excel without a header, in the TSV column order**: rejected. A spreadsheet's column order is easy to change by accident, and a shifted column would silently store phonetics as meanings; a header names every value.
- **Detect semicolon separators or GBK and other legacy encodings**: rejected. Detection guesses, and a wrong guess produces a preview that looks plausible. A semicolon file fails on its header with a message that says only commas are supported, and a non-UTF-8 file is refused with a message that says how to save it as UTF-8.
- **JSON Lines, NDJSON, or JSON5**: rejected. Each is a second syntax for data that the JSON array already carries, and JSON5 would need a parser dependency.
- **Accept the complete `{ bookId, entries }` request as JSON**: rejected. The book would then come from two places, the file and the page, and one of them would be silently ignored; the page's choice is the only one.
- **Accept snake_case aliases or convert numbers to strings**: rejected. Each makes the same data importable in more than one spelling, and a number such as `1.0` would not come back as it was written; refusing the item shows the problem where the administrator can fix it.
- **Use a CSV library**: rejected. The rules above take one small parser, and a dependency would add supply-chain and bundle cost for them.

## Consequences

- Administrators can import CSV files and pasted CSV text on `/import/batch`, as described in the [frontend specification](../frontend/README.md#batch-import-page).
- Administrators can import a JSON array of entries from a file or pasted text, with the API field names.
- Non-UTF-8 files, including TSV files, are refused instead of being imported with replacement characters.
- CSV formula injection is not addressed: the feature exports nothing, and imported values are stored as text as they are.
- Content that is in the wrong column under a correct header name is not detected; the administrator checks the preview.
- No server, API, SQLite, configuration, or container change is involved. Reverting the change removes CSV and restores the lenient decoding of TSV files.
