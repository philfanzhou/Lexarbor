import { parseVocabularyCsv } from './vocabularyCsv'
import { parseVocabularyJson } from './vocabularyJson'
import { parseVocabularyTsv } from './vocabularyTsv'
import type { VocabularyPreviewRow } from './vocabularyRow'

export type VocabularyInputFormat = 'tsv' | 'csv' | 'json'

export interface VocabularyParseResult {
  rows: VocabularyPreviewRow[]
  /** A file-level error; when it is set there are no rows and nothing can be submitted. */
  error?: string
}

/**
 * Parses batch import text in the given format. Every format gives the same
 * preview rows or a file-level error (ADR-006), so the page never branches on
 * the format.
 */
export function parseVocabularyInput(format: VocabularyInputFormat, text: string): VocabularyParseResult {
  switch (format) {
    case 'tsv':
      return { rows: parseVocabularyTsv(text) }
    case 'csv':
      return parseVocabularyCsv(text)
    case 'json':
      return parseVocabularyJson(text)
  }
}

const formatsByExtension = new Map<string, VocabularyInputFormat>([
  ['tsv', 'tsv'],
  ['txt', 'tsv'],
  ['csv', 'csv'],
  ['json', 'json']
])

/** The format a file name's extension selects, ignoring case; undefined when it selects none. */
export function formatForFileName(name: string): VocabularyInputFormat | undefined {
  const dot = name.lastIndexOf('.')
  return dot < 0 ? undefined : formatsByExtension.get(name.slice(dot + 1).toLowerCase())
}

/**
 * Decodes file content as strict UTF-8, removing a leading byte-order mark.
 * Throws a `TypeError` on any byte sequence that is not UTF-8, where a lenient
 * decoder would put U+FFFD in the preview and let it be submitted.
 */
export function decodeUtf8(buffer: ArrayBuffer): string {
  return new TextDecoder('utf-8', { fatal: true }).decode(buffer)
}
