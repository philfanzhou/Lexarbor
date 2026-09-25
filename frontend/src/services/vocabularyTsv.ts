import { toPreviewRow } from './vocabularyRow'
import type { VocabularyPreviewRow } from './vocabularyRow'

/**
 * Parses the TSV format of ADR-005 into one row per data line, in source order.
 *
 * Line numbers are physical: blank and comment lines are skipped but still
 * counted, so a number shown next to an invalid row is the line an editor shows.
 * A comment is a line whose first character is `#`, without trimming first,
 * which is how the removed seed parser read the same format.
 */
export function parseVocabularyTsv(text: string): VocabularyPreviewRow[] {
  const lines = text.replace(/^\uFEFF/, '').split(/\r\n|\n|\r/)
  const rows: VocabularyPreviewRow[] = []

  lines.forEach((line, index) => {
    if (line.trim() === '' || line.startsWith('#')) {
      return
    }

    rows.push(parseLine(line, index + 1))
  })

  return rows
}

function parseLine(line: string, lineNumber: number): VocabularyPreviewRow {
  const columns = line.split('\t').map((column) => column.trim())
  if (columns.length !== 5 && columns.length !== 6) {
    return { position: lineNumber, columns, error: `列数应为 5 或 6，实际为 ${columns.length}` }
  }

  return toPreviewRow(lineNumber, columns)
}
