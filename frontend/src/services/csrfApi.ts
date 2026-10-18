import type { AxiosRequestConfig } from 'axios'
import api from './api'

/**
 * The hosted-login antiforgery token service (B6): `GET /admin/auth/csrf`
 * answers `{"token": "..."}` together with the browser-bound antiforgery
 * cookie, and every unsafe admin request — the hosted logout included — must
 * echo the token back in the `X-SignaCore-CSRF` header. The pair is bound to
 * the cookie, not to a session, so one fetch is reused until a 401 retires it.
 */

let tokenPromise: Promise<string | null> | null = null
let pendingIsCurrent: (() => boolean) | undefined

async function fetchToken(lxIsCurrent?: () => boolean): Promise<string | null> {
  try {
    const body = await api.get<unknown>('/admin/auth/csrf', { lxIsCurrent })
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
export function ensureCsrfToken(lxIsCurrent?: () => boolean, signal?: AxiosRequestConfig['signal']): Promise<string | null> {
  // A completed token belongs to the browser cookie and remains reusable. An
  // in-flight fetch owned by an obsolete scope must neither redirect its
  // replacement nor make that replacement wait on an obsolete failure. Keep
  // this shared fetch independent of an individual consumer's abort signal.
  if (pendingIsCurrent?.() === false) tokenPromise = null
  if (!tokenPromise) {
    const pending = fetchToken(lxIsCurrent)
    tokenPromise = pending
    pendingIsCurrent = lxIsCurrent
    void pending.then(() => {
      if (tokenPromise !== pending) return
      if (lxIsCurrent?.() === false) tokenPromise = null
      pendingIsCurrent = undefined
    })
  }
  const pending = tokenPromise
  if (!signal) return pending
  // Cancel this consumer's wait immediately, even while the shared GET is
  // still pending. Axios then sees the aborted write signal before dispatch;
  // the shared cookie fetch remains usable by independent consumers.
  return new Promise((resolve) => {
    let settled = false
    const complete = (token: string | null) => {
      if (settled) return
      settled = true
      signal.removeEventListener?.('abort', onAbort)
      resolve(token)
    }
    const onAbort = () => complete(null)
    signal.addEventListener?.('abort', onAbort)
    if (signal.aborted) onAbort()
    void pending.then(complete)
  })
}

/** Retires the cached token after the session it accompanied is gone. */
export function resetCsrfToken(): void {
  tokenPromise = null
  pendingIsCurrent = undefined
}
