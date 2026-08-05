import api from './api'
import type { VocabularyMeaning } from '@/types'

export interface AddVocabularyPayload {
  word: { word: string; phoneticUk?: string; phoneticUs?: string }
  meaning: VocabularyMeaning
}

export const addVocabulary = (payload: AddVocabularyPayload) =>
  api.post('/admin/vocabulary', payload)
