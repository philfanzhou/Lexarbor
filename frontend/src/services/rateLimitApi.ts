import { ApiError } from './apiError'
import { isRevision, putWithRevision } from './settingsRevision'

interface RateLimitPolicy {
  enabled: boolean
  permitLimit: number
  windowSeconds: number
  source: 'default' | 'file' | 'environment' | 'commandLine'
  editable: boolean
}
interface RateLimitSettings {
  revision: string
  adminLogin: RateLimitPolicy
}
export interface RateLimitChanges {
  adminLoginEnabled: boolean
}

function parse(value: unknown): RateLimitSettings {
  const settings = value as Partial<RateLimitSettings> | null
  const validPolicy = (policy: RateLimitPolicy | undefined) => policy
    && typeof policy.enabled === 'boolean' && Number.isInteger(policy.permitLimit)
    && Number.isInteger(policy.windowSeconds) && typeof policy.editable === 'boolean'
    && ['default', 'file', 'environment', 'commandLine'].includes(policy.source)
  if (!settings || !isRevision(settings.revision) || !validPolicy(settings.adminLogin)) {
    throw new ApiError('无法读取有效的限流状态。')
  }
  return settings as RateLimitSettings
}

/** Saves the two live switches; the page shows their values through the settings catalog. */
export async function saveRateLimits(revision: string, changes: RateLimitChanges, signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await putWithRevision('/admin/system/settings/rate-limits', revision, changes, signal, isCurrent))
}
