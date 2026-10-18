import type { AxiosRequestConfig } from 'axios'
import api from './api'

/** Administrator catalog metadata; independent of pronunciation and public playback DTOs. */
export interface TextbookAudioTrack {
  id: string
  bookId: string
  unitId: string
  title: string
  displayOrder: number
  hasFile: boolean
  durationSeconds: number | null
  isAvailable: boolean
  version: string
}
export interface TextbookAudioPage {
  items: TextbookAudioTrack[]
  totalCount: number
  totalPage: number
}
export interface TextbookAudioCreate { title: string; displayOrder: number }
export interface TextbookAudioReplace extends TextbookAudioCreate {
  isAvailable: boolean
  expectedVersion: string
}
// Callers pass raw opaque IDs. Each path component is encoded exactly once here.
function catalogPath(bookId: string, unitId: string) {
  return `/admin/vocabulary-books/${encodeURIComponent(bookId)}/units/${encodeURIComponent(unitId)}/audio-tracks`
}
export function getTextbookAudioTracks(bookId: string, unitId: string, params: { page: number; size: number }, config: AxiosRequestConfig) {
  return api.get<TextbookAudioPage>(catalogPath(bookId, unitId), { ...config, params })
}
export function createTextbookAudioTrack(bookId: string, unitId: string, data: TextbookAudioCreate, config: AxiosRequestConfig) {
  return api.post<TextbookAudioTrack>(catalogPath(bookId, unitId), data, config)
}
export function replaceTextbookAudioTrack(bookId: string, unitId: string, trackId: string, data: TextbookAudioReplace, config: AxiosRequestConfig) {
  return api.put<TextbookAudioTrack>(`${catalogPath(bookId, unitId)}/${encodeURIComponent(trackId)}`, data, config)
}
export function removeTextbookAudioTrack(bookId: string, unitId: string, trackId: string, expectedVersion: string, config: AxiosRequestConfig) {
  return api.delete<{ success: boolean }>(`${catalogPath(bookId, unitId)}/${encodeURIComponent(trackId)}`, { ...config, params: { expectedVersion } })
}

export function getTextbookAudioUploadPolicy(bookId: string, unitId: string, config: AxiosRequestConfig) {
  return api.get<unknown>(`${catalogPath(bookId, unitId)}/upload-policy`, config)
}

/** Separate from pronunciation locale/sourceRevision playback contracts. */
export interface TextbookAudioPlayback { trackId: string; url: string; expiresAt: string }
export function getTextbookAudioPlayback(bookId: string, unitId: string, trackId: string, config: AxiosRequestConfig) {
  return api.get<unknown>(`${catalogPath(bookId, unitId)}/${encodeURIComponent(trackId)}/playback`, config)
}
export function uploadTextbookAudioContent(attempt: import('./textbookAudioUpload').TextbookUploadAttempt, config: AxiosRequestConfig) {
  // Each replay serializes the same immutable File and original condition. The
  // browser owns the multipart boundary; only this content call has no timeout.
  const data = new FormData()
  data.append('file', attempt.file)
  data.append('expectedVersion', attempt.expectedVersion)
  return api.put<unknown>(`${catalogPath(attempt.bookId, attempt.unitId)}/${encodeURIComponent(attempt.trackId)}/content`, data, {
    ...config, timeout: 0, headers: { ...config.headers, 'Idempotency-Key': attempt.key }
  })
}
