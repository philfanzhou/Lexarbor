import axios from 'axios'
import type { AxiosRequestConfig } from 'axios'
import { ApiError, getApiError, getEntryErrors } from './apiError'

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
  withCredentials: true,
  headers: { 'X-Requested-With': 'XMLHttpRequest' }
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
