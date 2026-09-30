import { ref } from 'vue'
import {
  createSession,
  deleteSession,
  getAuthMethod,
  getSession,
  readAuthMethod,
  readLogoutUrl
} from './authApi'
import type { AdminSession, AuthMethod } from './authApi'
import { getApiError } from './apiError'
import { ensureSessionVersionFetch, invalidateVersionState } from './systemVersion'

export const isAuthenticated = ref(false)
export const currentUser = ref<AdminSession | null>(null)

/**
 * The deployment's login mode, `null` until one method answer has been
 * accepted. This is deployment state, not session state: it survives a logout
 * so the login page renders the right mode immediately, and a late method
 * answer can never restore a session because it writes nothing else.
 */
export const authMethod = ref<AuthMethod | null>(null)

/**
 * Reads and caches the login mode. A failed or malformed answer throws; the
 * caller decides between a retryable error (the login page) and silence (the
 * best-effort prefetch). It never falls back to a guessed mode.
 */
export async function loadAuthMethod(): Promise<AuthMethod> {
  const method = readAuthMethod(await getAuthMethod())
  authMethod.value = method
  return method
}

/**
 * Best-effort prefetch, so a later logout can tell a hosted local-only logout
 * from a password-mode one without racing the notice against the navigation.
 * A failure stays silent: the login page and the logout classification each
 * read the mode again when they actually need it.
 */
export function ensureAuthMethodLoaded() {
  if (authMethod.value !== null) {
    return
  }

  void loadAuthMethod().catch(() => {})
}

/**
 * Hosted mode only on positive evidence. An unknown mode that cannot be read
 * either — the logout itself succeeded, so this is a rare follow-up failure —
 * keeps the previous silent local behaviour instead of inventing a provider
 * notice for a deployment that may not have one.
 */
export async function isHostedAuthMethod(): Promise<boolean> {
  if (authMethod.value !== null) {
    return authMethod.value === 'hosted'
  }

  try {
    return (await loadAuthMethod()) === 'hosted'
  } catch {
    return false
  }
}

function applySession(session: AdminSession) {
  currentUser.value = session
  isAuthenticated.value = true
  ensureAuthMethodLoaded()
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
