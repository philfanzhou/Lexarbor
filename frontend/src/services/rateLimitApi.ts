import api from './api'
import { ApiError } from './apiError'

export interface RateLimitPolicy {
  enabled: boolean
  permitLimit: number
  windowSeconds: number
  source: 'default' | 'file' | 'environment' | 'commandLine'
  editable: boolean
}
export interface RateLimitSettings {
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
  if (!settings || typeof settings.revision !== 'string'
    || !/^"[0-9a-f]{32}"$/.test(settings.revision)
    || !validPolicy(settings.adminLogin)) {
    throw new ApiError('无法读取有效的限流状态。')
  }
  return settings as RateLimitSettings
}

/** Saves the two live switches; the page shows their values through the settings catalog. */
export async function saveRateLimits(revision: string, changes: RateLimitChanges, signal: AbortSignal, isCurrent: () => boolean) {
  return parse(await api.put<unknown>('/admin/system/settings/rate-limits', changes,
    { headers: { 'If-Match': revision }, signal, lxIsCurrent: isCurrent }))
}
