import api from './api'
import { ApiError } from './apiError'

export interface AdminSession {
  username: string
  roles: string[]
}

/**
 * The two administration sign-in modes of `GET /admin/auth/method`: `hosted`
 * navigates the browser to SignaCore, `password` keeps the local form that the
 * backend exchanges server-side.
 */
export type AuthMethod = 'hosted' | 'password'

export function createSession(username: string, password: string) {
  return api.post<AdminSession>('/admin/auth/login', { username, password })
}

export function getAuthMethod() {
  return api.get<unknown>('/admin/auth/method')
}

/**
 * Reads the mode from the unwrapped `{ method }` body. Anything else — a
 * missing field, an unknown value, a non-object body — is a failure the caller
 * must surface as retryable; it must never be guessed to be password mode.
 */
export function readAuthMethod(data: unknown): AuthMethod {
  if (typeof data === 'object' && data !== null) {
    const method = (data as { method?: unknown }).method
    if (method === 'hosted' || method === 'password') {
      return method
    }
  }

  throw new ApiError('The login mode could not be read.')
}

export function getSession() {
  return api.get<AdminSession>('/admin/auth/session')
}

/**
 * `POST /admin/auth/logout` always revokes the local session first and answers
 * 200. The interceptor unwraps the envelope, so the caller sees `{ logoutUrl }`
 * only when hosted mode prepared a verified one-time upstream logout; every
 * other success — password modes, and hosted local-only — is the bare envelope.
 */
export function deleteSession() {
  return api.post<unknown>('/admin/auth/logout')
}

/** The verified upstream logout URI of a logout answer, or undefined. */
export function readLogoutUrl(data: unknown): string | undefined {
  if (typeof data === 'object' && data !== null) {
    const logoutUrl = (data as { logoutUrl?: unknown }).logoutUrl
    if (typeof logoutUrl === 'string' && logoutUrl.length > 0) {
      return logoutUrl
    }
  }

  return undefined
}
