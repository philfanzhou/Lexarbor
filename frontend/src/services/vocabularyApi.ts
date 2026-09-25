import api from './api'
import type { VocabularyMeaning } from '@/types'

export interface AddVocabularyPayload {
  word: { word: string; phoneticUk?: string; phoneticUs?: string }
  meaning: VocabularyMeaning
}

export const addVocabulary = (payload: AddVocabularyPayload) =>
  api.post('/admin/vocabulary', payload)

/** One entry of a batch; ADR-005 defines the fields and which are optional. */
export interface VocabularyBatchEntry {
  word: string
  phoneticUk?: string
  phoneticUs?: string
  partOfSpeech?: string
  meaning: string
  example?: string
}

export interface VocabularyBatchImportPayload {
  bookId: string
  entries: VocabularyBatchEntry[]
}

export interface VocabularyBatchImportResult {
  total: number
  created: number
  reused: number
}

export const importVocabularyBatch = (payload: VocabularyBatchImportPayload) =>
  api.post<VocabularyBatchImportResult>('/admin/vocabulary/batch', payload)
