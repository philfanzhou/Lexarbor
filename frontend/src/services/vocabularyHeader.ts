/**
 * The column names a header row may use, in canonical order. They are the TSV
 * column names of ADR-005, so an index here is also an index into
 * `VocabularyPreviewRow.columns`.
 */
export const VOCABULARY_COLUMNS = [
  'word',
  'phonetic_uk',
  'phonetic_us',
  'part_of_speech',
  'meaning',
  'example'
] as const

const REQUIRED_COLUMNS = ['word', 'meaning'] as const

/** Where each source column goes: a canonical index, or -1 for a blank column that is ignored. */
export type VocabularyHeaderMapping = number[]

export type VocabularyHeaderResult =
  | { mapping: VocabularyHeaderMapping; error?: undefined }
  | { mapping?: undefined; error: string }

/**
 * Resolves a header row by the rules of ADR-006, shared by every format that
 * has one. Names are trimmed and matched without regard to case, in any order.
 * A column with a blank name is ignored only when every data value under it is
 * blank too; anything else a header cannot account for fails the whole input,
 * so no column is ever dropped without the administrator knowing.
 *
 * `unknownHint` is appended to the message for an unrecognized name, for a
 * format that knows the likely cause.
 */
export function resolveVocabularyHeader(
  header: string[],
  records: string[][],
  unknownHint = ''
): VocabularyHeaderResult {
  const mapping: VocabularyHeaderMapping = []
  const seen = new Map<number, number>()

  for (const [index, raw] of header.entries()) {
    const columnNumber = index + 1
    const name = raw.trim()
    if (name === '') {
      if (records.some((fields) => (fields[index] ?? '').trim() !== '')) {
        return { error: `第 ${columnNumber} 列表头为空，但该列有数据` }
      }
      mapping.push(-1)
      continue
    }

    const canonical = (VOCABULARY_COLUMNS as readonly string[]).indexOf(name.toLowerCase())
    if (canonical < 0) {
      return {
        error: `第 ${columnNumber} 列表头「${name}」无法识别；支持：${VOCABULARY_COLUMNS.join('、')}${unknownHint}`
      }
    }

    const first = seen.get(canonical)
    if (first !== undefined) {
      return { error: `第 ${columnNumber} 列表头「${name}」与第 ${first} 列重复` }
    }
    seen.set(canonical, columnNumber)
    mapping.push(canonical)
  }

  for (const required of REQUIRED_COLUMNS) {
    if (!seen.has(VOCABULARY_COLUMNS.indexOf(required))) {
      return { error: `表头缺少必需的 ${required} 列` }
    }
  }

  return { mapping }
}
