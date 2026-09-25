import { toPreviewRow } from './vocabularyRow'
import type { VocabularyPreviewRow } from './vocabularyRow'

/**
 * The field names an item may use, in canonical order: the `VocabularyBatchEntry`
 * names of the API, so an index here is also an index into
 * `VocabularyPreviewRow.columns`.
 */
const JSON_FIELDS = ['word', 'phoneticUk', 'phoneticUs', 'partOfSpeech', 'meaning', 'example'] as const

export interface VocabularyJsonResult {
  rows: VocabularyPreviewRow[]
  /** A file-level error; when it is set there are no rows. */
  error?: string
}

/**
 * Parses a JSON array of entries, by the rules of ADR-006, into one row per
 * item in array order. A row's position is the item's number, from 1.
 *
 * An item is never repaired: an unknown field or a value that is not a string
 * makes it invalid rather than being dropped or converted, so what is sent is
 * exactly what the array says.
 */
export function parseVocabularyJson(text: string): VocabularyJsonResult {
  const source = text.replace(/^\uFEFF/, '')
  // An empty text area is no input yet, not a syntax error.
  if (source.trim() === '') {
    return { rows: [] }
  }

  let value: unknown
  try {
    value = JSON.parse(source)
  } catch (error: unknown) {
    // The browser's message carries the position, e.g. "at position 14 (line 1 column 15)".
    return { rows: [], error: `JSON 语法错误：${error instanceof Error ? error.message : String(error)}` }
  }

  if (!Array.isArray(value)) {
    const isPayload = isObject(value) && Object.hasOwn(value, 'entries')
    return {
      rows: [],
      error: isPayload
        ? 'JSON 顶层必须是数组；不接受 { bookId, entries } 载荷，请只提供 entries 数组；教材以页面选择为准'
        : 'JSON 顶层必须是数组'
    }
  }

  return { rows: value.map((item: unknown, index) => parseItem(item, index + 1)) }
}

function parseItem(item: unknown, position: number): VocabularyPreviewRow {
  if (!isObject(item)) {
    return { position, columns: JSON_FIELDS.map(() => ''), error: '该项不是对象' }
  }

  const reasons: string[] = []
  // Object.keys, not `in`: JSON.parse makes "__proto__" an own key, and it is
  // as unknown as any other name.
  const unknown = Object.keys(item).filter((key) => !(JSON_FIELDS as readonly string[]).includes(key))
  if (unknown.length > 0) {
    reasons.push(`未知字段：${unknown.join('、')}`)
  }

  const columns = JSON_FIELDS.map((field) => {
    const fieldValue = Object.hasOwn(item, field) ? item[field] : undefined
    if (fieldValue === undefined || fieldValue === null) {
      return ''
    }
    if (typeof fieldValue === 'string') {
      return fieldValue.trim()
    }
    // Shown as written, so the administrator sees the value that was refused.
    reasons.push(`字段 ${field} 应为字符串`)
    return JSON.stringify(fieldValue)
  })

  const row = toPreviewRow(position, columns)
  if (reasons.length === 0) {
    return row
  }
  if (row.error !== undefined) {
    reasons.push(row.error)
  }
  return { position, columns, error: reasons.join('；') }
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
