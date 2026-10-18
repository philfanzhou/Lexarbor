import { ApiError } from './apiError'

export interface TextbookUploadPolicy { enabled: boolean; maxUploadBytes: number; maxDurationSeconds: number }
export interface TextbookUploadReceipt { trackId: string; version: string; durationSeconds: number }
export interface TextbookUploadAttempt {
  readonly bookId: string
  readonly unitId: string
  readonly trackId: string
  readonly expectedVersion: string
  readonly file: File
  readonly key: string
}
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/
function object(value: unknown): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new ApiError('Unreadable upload response')
  return value as Record<string, unknown>
}
export function readTextbookUploadPolicy(value: unknown): TextbookUploadPolicy {
  const data = object(value)
  if (Object.keys(data).sort().join(',') !== 'enabled,maxDurationSeconds,maxUploadBytes'
    || typeof data.enabled !== 'boolean' || !Number.isSafeInteger(data.maxUploadBytes)
    || (data.maxUploadBytes as number) < 1 || (data.maxUploadBytes as number) > 128 * 1024 * 1024
    || typeof data.maxDurationSeconds !== 'number' || !Number.isFinite(data.maxDurationSeconds) || data.maxDurationSeconds <= 0) {
    throw new ApiError('Unreadable upload policy')
  }
  return data as unknown as TextbookUploadPolicy
}
export function readTextbookUploadReceipt(value: unknown, trackId: string): TextbookUploadReceipt {
  const data = object(value)
  if (Object.keys(data).sort().join(',') !== 'durationSeconds,trackId,version'
    || data.trackId !== trackId || typeof data.version !== 'string' || !uuid.test(data.version)
    || typeof data.durationSeconds !== 'number' || !Number.isFinite(data.durationSeconds) || data.durationSeconds <= 0) {
    throw new ApiError('Unreadable upload receipt')
  }
  return data as unknown as TextbookUploadReceipt
}
export function createTextbookUploadAttempt(bookId: string, unitId: string, trackId: string, expectedVersion: string, file: File): TextbookUploadAttempt {
  return Object.freeze({ bookId, unitId, trackId, expectedVersion, file, key: crypto.randomUUID() })
}
