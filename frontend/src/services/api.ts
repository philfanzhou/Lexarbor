import axios from 'axios'
import type { AxiosRequestConfig } from 'axios'
import { ApiError, getApiError } from './apiError'

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
      return Promise.reject(new ApiError(body.message || 'Request failed', resp.status))
    }
    return body?.data ?? body
  },
  (error: unknown) => {
    const apiError = getApiError(error)
    if (apiError.status === 401) {
      onUnauthorized()
    } else if (apiError.status === 403) {
      onForbidden()
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
