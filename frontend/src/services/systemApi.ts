import api from './api'

/** The three build-identity channels the Host assembly can report. */
export type BuildChannel = 'release' | 'edge' | 'development'

/** The authorized `GET /admin/system/version` payload. */
export interface SystemVersion {
  version: string
  revision: string | null
  channel: BuildChannel
}

/**
 * A success body counts as a version only when it has the documented shape:
 * a non-blank version string, a revision that is null or a full lowercase SHA,
 * and one of the three known channels. Anything else — an HTML error page, a
 * truncated proxy answer, a missing field — is shown as 版本未知, never as a
 * guessed identity.
 */
export function isSystemVersion(value: unknown): value is SystemVersion {
  if (typeof value !== 'object' || value === null) {
    return false
  }

  const { version, revision, channel } = value as Record<string, unknown>
  return (
    typeof version === 'string' &&
    version.trim() !== '' &&
    (revision === null || (typeof revision === 'string' && /^[0-9a-f]{40}$/.test(revision))) &&
    (channel === 'release' || channel === 'edge' || channel === 'development')
  )
}

export interface SystemVersionRequestOptions {
  signal?: AbortSignal
  isCurrent?: () => boolean
}

export function getSystemVersion(options: SystemVersionRequestOptions = {}) {
  return api.get<unknown>('/admin/system/version', {
    signal: options.signal,
    lxIsCurrent: options.isCurrent
  })
}
