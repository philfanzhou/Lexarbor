import type { VocabularyBatchEntry } from './vocabularyApi'

/**
 * One data row of the batch import preview, whatever format it was parsed from.
 * `position` is where the row starts in the source, as the format defines it
 * (ADR-006); `columns` holds the trimmed values in the order `word`,
 * `phonetic_uk`, `phonetic_us`, `part_of_speech`, `meaning`, `example`, for
 * display. `entry` is set only when the row is valid, and `error` only when it
 * is not.
 */
export interface VocabularyPreviewRow {
  position: number
  columns: string[]
  entry?: VocabularyBatchEntry
  error?: string
}

/**
 * Checks one row whose values are already trimmed and in the canonical order,
 * and builds its entry. Every format ends here, so the same data gives the same
 * entry whichever format it was imported from.
 */
export function toPreviewRow(position: number, columns: string[]): VocabularyPreviewRow {
  const [word, phoneticUk, phoneticUs, partOfSpeech, meaning, example = ''] = columns
  const reasons: string[] = []
  if (!word) {
    reasons.push('缺少单词')
  }
  if (!meaning) {
    reasons.push('缺少释义')
  }
  if (reasons.length > 0) {
    return { position, columns, error: reasons.join('；') }
  }

  // A blank optional column is left out rather than sent as an empty string,
  // the same as the single-entry form.
  const entry: VocabularyBatchEntry = { word, meaning }
  if (phoneticUk) entry.phoneticUk = phoneticUk
  if (phoneticUs) entry.phoneticUs = phoneticUs
  if (partOfSpeech) entry.partOfSpeech = partOfSpeech
  if (example) entry.example = example

  return { position, columns, entry }
}
