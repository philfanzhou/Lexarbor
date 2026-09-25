import { resolveVocabularyHeader, VOCABULARY_COLUMNS } from './vocabularyHeader'
import { toPreviewRow } from './vocabularyRow'
import type { VocabularyPreviewRow } from './vocabularyRow'

interface CsvRecord {
  /** The physical line the record starts on, from 1. */
  line: number
  fields: string[]
}

type CsvRecordsResult =
  | { records: CsvRecord[]; error?: undefined }
  | { records?: undefined; error: string }

export interface VocabularyCsvResult {
  rows: VocabularyPreviewRow[]
  /** A file-level error; when it is set there are no rows. */
  error?: string
}

/**
 * Parses comma-separated values with a header row, by the rules of ADR-006, into
 * one row per data record in source order.
 *
 * A row's position is the physical line its record starts on. Records whose
 * fields are all blank are skipped but their lines still count, and a line
 * break inside a quoted field moves every later record down.
 */
export function parseVocabularyCsv(text: string): VocabularyCsvResult {
  const parsed = readRecords(text.replace(/^\uFEFF/, ''))
  if (parsed.error !== undefined) {
    return { rows: [], error: parsed.error }
  }

  const [header, ...data] = parsed.records.filter((record) =>
    record.fields.some((field) => field.trim() !== ''))
  if (!header) {
    return { rows: [] }
  }

  const resolved = resolveVocabularyHeader(
    header.fields,
    data.map((record) => record.fields),
    '（CSV 只支持逗号分隔）'
  )
  if (resolved.error !== undefined) {
    return { rows: [], error: resolved.error }
  }

  const rows = data.map((record) => {
    const columns: string[] = VOCABULARY_COLUMNS.map(() => '')
    resolved.mapping.forEach((canonical, index) => {
      if (canonical >= 0) {
        columns[canonical] = (record.fields[index] ?? '').trim()
      }
    })

    if (record.fields.length !== header.fields.length) {
      return {
        position: record.line,
        columns,
        error: `列数应为 ${header.fields.length}，实际为 ${record.fields.length}`
      }
    }
    return toPreviewRow(record.line, columns)
  })

  return { rows }
}

/**
 * Splits RFC 4180 text into records in one linear pass. Only a field whose first
 * character is `"` is quoted; inside it commas and line breaks are content and
 * `""` is one quote. Every line break inside a quoted field becomes `\n`, so a
 * file and the same text pasted into a text area, which normalizes line breaks,
 * give the same values.
 */
function readRecords(text: string): CsvRecordsResult {
  const records: CsvRecord[] = []
  const length = text.length
  let i = 0
  let line = 1

  while (i < length) {
    const record: CsvRecord = { line, fields: [] }

    for (;;) {
      if (text[i] === '"') {
        const quoteLine = line
        const parts: string[] = []
        let start = ++i
        for (;;) {
          if (i >= length) {
            return { error: `第 ${quoteLine} 行开始的引号没有闭合` }
          }
          const char = text[i]
          if (char === '"') {
            parts.push(text.slice(start, i))
            if (text[i + 1] === '"') {
              parts.push('"')
              i += 2
              start = i
              continue
            }
            i++
            break
          }
          if (char === '\r' || char === '\n') {
            parts.push(text.slice(start, i), '\n')
            i += char === '\r' && text[i + 1] === '\n' ? 2 : 1
            line++
            start = i
            continue
          }
          i++
        }
        record.fields.push(parts.join(''))

        const next = text[i]
        if (i < length && next !== ',' && next !== '\r' && next !== '\n') {
          return { error: `第 ${line} 行：引号字段结束后应为逗号或换行` }
        }
      } else {
        const start = i
        while (i < length && text[i] !== ',' && text[i] !== '\r' && text[i] !== '\n') {
          i++
        }
        record.fields.push(text.slice(start, i))
      }

      if (text[i] !== ',') {
        break
      }
      i++
    }

    records.push(record)
    if (i < length) {
      i += text[i] === '\r' && text[i + 1] === '\n' ? 2 : 1
      line++
    }
  }

  return { records }
}
