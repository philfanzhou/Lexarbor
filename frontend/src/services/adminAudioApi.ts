import type { AxiosRequestConfig } from 'axios'
import api from './api'

export type AudioLocale = 'en-GB' | 'en-US'
export type AudioStatus = 'missing' | 'ready' | 'stale'
export interface AudioBinding { assetId: string; sourceRevision: string }
export interface AudioTarget {
  wordId: string
  meaningId: string | null
  locale: AudioLocale
  targetType: 'default' | 'meaning'
  status: AudioStatus
  source: 'default' | 'meaning' | null
  sourceRevision: string
  input: { word: string; phonetic: string | null; partOfSpeech: string | null; meaning: string | null }
  ownBinding: AudioBinding | null
  effectiveBinding: AudioBinding | null
}
export interface AudioMetadata { enabled: boolean; items: AudioTarget[] }
export interface AudioPlayback {
  locale: AudioLocale
  status: AudioStatus
  source: 'default' | 'meaning' | null
  assetId: string | null
  sourceRevision: string | null
  url: string | null
  expiresAt: string | null
}

export function getAdminAudio(wordId: string, meaningId: string | null, config: AxiosRequestConfig) {
  return api.get<AudioMetadata>(`/admin/vocabulary/${encodeURIComponent(wordId)}/audio`, {
    ...config, params: meaningId === null ? undefined : { meaningId }
  })
}

/** Short-lived credentials belong only to the active playback attempt. */
export function getAdminAudioPlayback(wordId: string, meaningId: string | null, locale: AudioLocale, config: AxiosRequestConfig) {
  return api.get<AudioPlayback>(`/admin/vocabulary/${encodeURIComponent(wordId)}/audio/${locale}/playback`, {
    ...config, params: meaningId === null ? undefined : { meaningId }
  })
}
