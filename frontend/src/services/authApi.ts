import api from './api'

export interface AdminSession {
  username: string
  roles: string[]
}

export function getSession() {
  return api.get<AdminSession>('/admin/auth/session')
}

/**
 * `POST /admin/auth/logout` always revokes the local session first and answers
 * 200. The interceptor unwraps the envelope, so the caller sees `{ logoutUrl }`
 * only when the hosted flow prepared a verified one-time upstream logout; every
 * other success is the bare envelope and means a local-only logout.
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
