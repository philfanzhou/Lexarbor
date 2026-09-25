import type { VocabularyBatchEntry } from './vocabularyApi'

/**
 * One data line of a vocabulary TSV. `columns` holds the trimmed values as
 * written, for display; `entry` is set only when the line is valid, and `error`
 * only when it is not.
 */
export interface VocabularyTsvRow {
  lineNumber: number
  columns: string[]
  entry?: VocabularyBatchEntry
  error?: string
}

/**
 * Parses the TSV format of ADR-005 into one row per data line, in source order.
 *
 * Line numbers are physical: blank and comment lines are skipped but still
 * counted, so a number shown next to an invalid row is the line an editor shows.
 * A comment is a line whose first character is `#`, without trimming first,
 * which is how the removed seed parser read the same format.
 */
export function parseVocabularyTsv(text: string): VocabularyTsvRow[] {
  const lines = text.replace(/^\uFEFF/, '').split(/\r\n|\n|\r/)
  const rows: VocabularyTsvRow[] = []

  lines.forEach((line, index) => {
    if (line.trim() === '' || line.startsWith('#')) {
      return
    }

    rows.push(parseLine(line, index + 1))
  })

  return rows
}

function parseLine(line: string, lineNumber: number): VocabularyTsvRow {
  const columns = line.split('\t').map((column) => column.trim())
  if (columns.length !== 5 && columns.length !== 6) {
    return { lineNumber, columns, error: `列数应为 5 或 6，实际为 ${columns.length}` }
  }

  const [word, phoneticUk, phoneticUs, partOfSpeech, meaning, example = ''] = columns
  const reasons: string[] = []
  if (!word) {
    reasons.push('缺少单词')
  }
  if (!meaning) {
    reasons.push('缺少释义')
  }
  if (reasons.length > 0) {
    return { lineNumber, columns, error: reasons.join('；') }
  }

  // A blank optional column is left out rather than sent as an empty string,
  // the same as the single-entry form.
  const entry: VocabularyBatchEntry = { word, meaning }
  if (phoneticUk) entry.phoneticUk = phoneticUk
  if (phoneticUs) entry.phoneticUs = phoneticUs
  if (partOfSpeech) entry.partOfSpeech = partOfSpeech
  if (example) entry.example = example

  return { lineNumber, columns, entry }
}
