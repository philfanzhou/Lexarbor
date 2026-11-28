import { ApiError } from './apiError'
import type { CredentialChange } from './audioStorageSettingsApi'
import { isRevision, putWithRevision } from './settingsRevision'

/**
 * Changes to the review assistant or enrichment, keyed by the module's dotted names
 * (`reviewAssistant.model`, `enrichment.tts.uk.speed`, …). Which names exist, their
 * bounds and their sources come from the settings catalog, not from this file.
 */
export type SettingValue = string | number | boolean | null
export type EnrichmentChanges = Record<string, SettingValue | CredentialChange>

function parse(value: unknown) {
  const state = value as Partial<{ revision: string }> | null
  // The page shows these values through the settings catalog; the save's answer only
  // has to be the module's own well-formed reply.
  if (!state || !isRevision(state.revision)) {
    throw new ApiError('无法读取有效的审核与补齐配置。')
  }
  return state as { revision: string }
}

/** The same changes in the module's nested request shape. */
export function enrichmentChangesBody(changes: EnrichmentChanges) {
  const body: Record<string, unknown> = {}
  for (const [name, value] of Object.entries(changes)) {
    const path = name.split('.'), leaf = path.pop()!
    let parent = body
    for (const part of path) {
      parent[part] ??= {}
      parent = parent[part] as Record<string, unknown>
    }
    parent[leaf] = value
  }
  return body
}

export async function saveEnrichmentSettings(revision: string, changes: EnrichmentChanges, signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await putWithRevision('/admin/enrichment/settings', revision, enrichmentChangesBody(changes), signal, isCurrent))
}
