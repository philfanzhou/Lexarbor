import { ref } from 'vue'
import { createSession, deleteSession, getSession } from './authApi'
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

export async function login(username: string, password: string): Promise<void> {
  const session = await createSession(username, password)
  applySession(session)
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

export async function logout(): Promise<void> {
  // The version request must not outlive the session it described, even while
  // the logout call itself is still in flight; the finally below still clears
  // the session on both success and failure.
  invalidateVersionState()
  try {
    await deleteSession()
  } finally {
    clearSession()
  }
}
