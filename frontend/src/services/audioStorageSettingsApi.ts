import { ApiError } from './apiError'
import { isRevision, putWithRevision } from './settingsRevision'

export const audioTextFields = ['internalEndpoint', 'publicEndpoint', 'region', 'bucket', 'keyPrefix'] as const
export const credentialFields = ['accessKey', 'secretKey'] as const
export type CredentialAction = 'keep' | 'replace' | 'clear'
export type CredentialChange = { action: 'keep' | 'clear' } | { action: 'replace'; value: string }
export type AudioStorageChanges = Partial<Record<(typeof audioTextFields)[number], string>> & {
  enabled?: boolean
  accessKey?: CredentialChange
  secretKey?: CredentialChange
}
function parse(value: unknown) {
  const state = value as Partial<{ revision: string }> | null
  const invalid = () => { throw new ApiError('无法读取有效的音频存储状态。') }
  // The page shows these values through the settings catalog; the save's answer only
  // has to be the module's own well-formed reply.
  if (!state || !isRevision(state.revision)) return invalid()
  return state as { revision: string }
}

/** Saves the live storage fields; the page shows their values through the settings catalog. */
export async function saveAudioStorage(revision: string, changes: AudioStorageChanges, signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await putWithRevision('/admin/system/settings/audio-storage', revision, changes, signal, isCurrent))
}
