import { computed, ref } from 'vue'
import { getSystemVersion, isSystemVersion } from './systemApi'
import type { SystemVersion } from './systemApi'

/**
 * Session-scoped build-version state for the administration header.
 *
 * One non-blocking request per administrator session generation: a generation
 * starts when `applySession` accepts a session (login or restore) and ends at
 * the first `clearSession`, which also happens when a logout begins. Only the
 * current generation may write state, and its in-flight request is aborted and
 * marked stale the moment the generation ends, so a late success or a late
 * 401/403 can neither repaint the version nor clear the session that replaced
 * it. Nothing is persisted and nothing retries within a session.
 */

export type VersionStatus = 'idle' | 'loading' | 'ready' | 'unknown'

export const versionStatus = ref<VersionStatus>('idle')
export const versionInfo = ref<SystemVersion | null>(null)

let generation = 0
let controller: AbortController | null = null
let fetchedForSession = false

function beginFetch() {
  generation += 1
  const current = generation

  controller?.abort()
  controller = new AbortController()

  versionStatus.value = 'loading'
  versionInfo.value = null

  getSystemVersion({
    signal: controller.signal,
    isCurrent: () => current === generation
  }).then(
    (data: unknown) => {
      if (current !== generation) {
        return
      }

      if (isSystemVersion(data)) {
        versionInfo.value = data
        versionStatus.value = 'ready'
      } else {
        versionStatus.value = 'unknown'
      }
    },
    () => {
      // Any failure — network, timeout, 404 from an older backend, 5xx, a
      // malformed body — reads as 版本未知 and leaves the session working.
      // A failure of the current generation's own 401/403 has already been
      // through the global auth handler, which ends this generation here.
      if (current !== generation) {
        return
      }

      versionStatus.value = 'unknown'
    }
  )
}

/**
 * Fetch the version once per session generation. `applySession` runs again
 * when the route guard restores the same freshly created session; that is the
 * same generation and must not send a second request.
 */
export function ensureSessionVersionFetch() {
  if (fetchedForSession) {
    return
  }

  fetchedForSession = true
  beginFetch()
}

/**
 * End the current generation: abort its request, drop its state, and mark the
 * guard in flight as stale so the interceptor skips the global auth handling
 * for it. Called when a session is cleared and when a logout begins.
 */
export function invalidateVersionState() {
  generation += 1
  fetchedForSession = false
  controller?.abort()
  controller = null
  versionStatus.value = 'idle'
  versionInfo.value = null
}

function shortRevision(revision: string) {
  return `edge · ${revision.slice(0, 7)}`
}

/** The low-key header label, per the build-identity model of issue #92. */
export const versionLabel = computed(() => {
  const info = versionInfo.value
  if (versionStatus.value !== 'ready' || !info) {
    return versionStatus.value === 'loading' ? '版本获取中' : '版本未知'
  }

  if (info.version === 'unknown') {
    return '版本未知'
  }

  if (info.channel === 'release') {
    return `v${info.version}`
  }

  if (info.channel === 'edge') {
    return info.revision ? shortRevision(info.revision) : 'edge · 提交未知'
  }

  return '开发版本'
})
