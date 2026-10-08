import api from './api'

/**
 * The hosted-login antiforgery token service (B6): `GET /admin/auth/csrf`
 * answers `{"token": "..."}` together with the browser-bound antiforgery
 * cookie, and every unsafe admin request — the hosted logout included — must
 * echo the token back in the `X-SignaCore-CSRF` header. The pair is bound to
 * the cookie, not to a session, so one fetch is reused until a 401 retires it.
 */

let tokenPromise: Promise<string | null> | null = null

async function fetchToken(): Promise<string | null> {
  try {
    const body = await api.get<unknown>('/admin/auth/csrf')
    if (typeof body === 'object' && body !== null) {
      const token = (body as { token?: unknown }).token
      if (typeof token === 'string' && token.length > 0) {
        return token
      }
    }
    return null
  } catch {
    // The server owns the boundary: without a token the request still goes
    // out, and the server's answer is the authoritative one.
    return null
  }
}

/** Returns the cached antiforgery token, fetching it once per token lifetime. */
export function ensureCsrfToken(): Promise<string | null> {
  tokenPromise ??= fetchToken()
  return tokenPromise
}

/** Retires the cached token after the session it accompanied is gone. */
export function resetCsrfToken(): void {
  tokenPromise = null
}
