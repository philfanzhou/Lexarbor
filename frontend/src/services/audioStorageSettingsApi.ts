import api from './api'
import { ApiError } from './apiError'

export const audioTextFields = ['internalEndpoint', 'publicEndpoint', 'region', 'bucket', 'keyPrefix'] as const
export const credentialFields = ['accessKey', 'secretKey'] as const
export type AudioTextField = typeof audioTextFields[number]
export type CredentialField = typeof credentialFields[number]
export type AudioField = AudioTextField | CredentialField | 'enabled'
export type CredentialAction = 'keep' | 'replace' | 'clear'
export interface AudioFieldState {
  source: 'default' | 'file' | 'environment' | 'commandLine'
  editable: boolean
  reason: string | null
}
export interface AudioStorageSettings extends Record<AudioTextField, string> {
  revision: string
  enabled: boolean
  hasAccessKey: boolean
  hasSecretKey: boolean
  fields: Record<AudioField, AudioFieldState>
}
export type CredentialChange = { action: 'keep' | 'clear' } | { action: 'replace'; value: string }
export type AudioStorageChanges = Partial<Record<AudioTextField, string>> & {
  enabled?: boolean
  accessKey?: CredentialChange
  secretKey?: CredentialChange
}
function parse(value: unknown): AudioStorageSettings {
  const state = value as Partial<AudioStorageSettings> | null
  const invalid = () => { throw new ApiError('无法读取有效的音频存储状态。') }
  if (!state || typeof state.revision !== 'string' || !/^"[0-9a-f]{32}"$/.test(state.revision)
    || typeof state.enabled !== 'boolean' || typeof state.hasAccessKey !== 'boolean' || typeof state.hasSecretKey !== 'boolean'
    || !state.fields || audioTextFields.some(key => typeof state[key] !== 'string')) return invalid()
  const fields = {} as Record<AudioField, AudioFieldState>
  for (const key of ['enabled', ...audioTextFields, ...credentialFields] as const) {
    const field = state.fields[key]
    if (!field || !['default', 'file', 'environment', 'commandLine'].includes(field.source)
      || typeof field.editable !== 'boolean' || (field.reason !== null && typeof field.reason !== 'string')) return invalid()
    fields[key] = { source: field.source, editable: field.editable, reason: field.reason }
  }
  // Copy the public contract only. Never retain unexpected response properties.
  return { revision: state.revision, enabled: state.enabled, hasAccessKey: state.hasAccessKey, hasSecretKey: state.hasSecretKey,
    internalEndpoint: state.internalEndpoint!, publicEndpoint: state.publicEndpoint!, region: state.region!, bucket: state.bucket!, keyPrefix: state.keyPrefix!, fields }
}
const url = '/admin/system/settings/audio-storage'
export async function readAudioStorage(signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await api.get<unknown>(url, { signal, lxIsCurrent: isCurrent }))
}
export async function saveAudioStorage(revision: string, changes: AudioStorageChanges, signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await api.put<unknown>(url, changes, { headers: { 'If-Match': revision }, signal, lxIsCurrent: isCurrent }))
}
