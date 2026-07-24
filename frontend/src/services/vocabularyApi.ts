import api from './api'
import type { VocabularyMeaning } from '@/types'

export interface AddVocabularyPayload {
  word: { word: string; phonetic?: string }
  meaning: VocabularyMeaning
}

export const addVocabulary = (payload: AddVocabularyPayload) =>
  api.post('/admin/vocabulary', payload)
