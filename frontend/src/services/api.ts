import axios from 'axios'
import type { AxiosRequestConfig } from 'axios'
import { ApiError, getApiError, getEntryErrors } from './apiError'
import { ensureCsrfToken, resetCsrfToken } from './csrfApi'

declare module 'axios' {
  export interface AxiosRequestConfig {
    /**
     * Optional session-scoped guard, honoured before the global 401/403
     * handling below. Returning false means the request no longer belongs to
     * the current session (for example the administrator signed out while it
     * was in flight), so its failure must not clear or redirect the session
     * that replaced it. Requests without the guard keep the default behaviour.
     */
    lxIsCurrent?: () => boolean
  }
}

type AuthFailureHandler = () => void

let onUnauthorized: AuthFailureHandler = () => {}
let onForbidden: AuthFailureHandler = () => {}

export function setAuthFailureHandlers(
  unauthorizedHandler: AuthFailureHandler,
  forbiddenHandler: AuthFailureHandler
) {
  onUnauthorized = unauthorizedHandler
  onForbidden = forbiddenHandler
}

const api = axios.create({
  timeout: 30000,
  withCredentials: true
})

/**
 * Every unsafe method (POST/PUT/PATCH/DELETE, including the hosted logout)
 * must carry the antiforgery token from `GET /admin/auth/csrf` in the request
 * header the server validates. The token is fetched once and reused while the
 * browser keeps its antiforgery cookie; a failed fetch still lets the request
 * through without the header so the server — the authority of the boundary —
 * answers it, instead of the client silently swallowing the call.
 */
const safeMethods = new Set(['get', 'head', 'options', 'trace'])

api.interceptors.request.use(async (config) => {
  if (!safeMethods.has((config.method ?? 'get').toLowerCase())) {
    const token = await ensureCsrfToken()
    if (token) {
      config.headers.set('X-SignaCore-CSRF', token)
    }
  }
  return config
})

api.interceptors.response.use(
  (resp) => {
    const body = resp.data
    if (body && body.success === false) {
      return Promise.reject(
        new ApiError(body.message || 'Request failed', resp.status, getEntryErrors(body.errors))
      )
    }
    return body?.data ?? body
  },
  (error: unknown) => {
    const apiError = getApiError(error)
    const staleGuard = axios.isAxiosError(error) ? error.config?.lxIsCurrent : undefined
    if (apiError.status === 401) {
      // The session the token was issued alongside is gone; the next write
      // must fetch a fresh pair instead of replaying the stale token.
      resetCsrfToken()
      if (staleGuard?.() !== false) {
        onUnauthorized()
      }
    } else if (apiError.status === 403) {
      if (staleGuard?.() !== false) {
        onForbidden()
      }
    }

    return Promise.reject(apiError)
  }
)

interface ApiClient {
  get<T>(url: string, config?: AxiosRequestConfig): Promise<T>
  post<T>(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<T>
  put<T>(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<T>
  delete<T>(url: string, config?: AxiosRequestConfig): Promise<T>
}

export default api as unknown as ApiClient
