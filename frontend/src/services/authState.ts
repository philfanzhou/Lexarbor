import { ref } from 'vue'
import { deleteSession, getSession, readLogoutUrl } from './authApi'
import type { AdminSession } from './authApi'
import { getApiError } from './apiError'
import { ensureSessionVersionFetch, invalidateVersionState } from './systemVersion'

export const isAuthenticated = ref(false)
export const currentUser = ref<AdminSession | null>(null)

function applySession(session: AdminSession) {
  currentUser.value = session
  isAuthenticated.value = true
  ensureSessionVersionFetch()
}

export function clearSession() {
  currentUser.value = null
  isAuthenticated.value = false
  invalidateVersionState()
}

export async function restoreSession(): Promise<void> {
  try {
    applySession(await getSession())
  } catch (error: unknown) {
    clearSession()
    if (getApiError(error).status === 401) {
      return
    }

    throw error
  }
}

/** What the logout answer allows the caller to do next. */
export interface LogoutOutcome {
  /**
   * The verified one-time SignaCore logout URI, present only when hosted mode
   * prepared an upstream logout. The caller completes it with a top-level
   * browser navigation; without it the logout was local-only.
   */
  logoutUrl?: string
}

export async function logout(): Promise<LogoutOutcome> {
  // The version request must not outlive the session it described, even while
  // the logout call itself is still in flight; the finally below still clears
  // the session on both success and failure.
  invalidateVersionState()
  try {
    return { logoutUrl: readLogoutUrl(await deleteSession()) }
  } finally {
    clearSession()
  }
}
