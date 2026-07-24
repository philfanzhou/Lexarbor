import axios from 'axios'
import type { AxiosRequestConfig } from 'axios'

const api = axios.create({ timeout: 30000 })

api.interceptors.response.use(
  (resp) => {
    const body = resp.data
    if (body && body.success === false) {
      return Promise.reject(new Error(body.message || 'Request failed'))
    }
    return body?.data ?? body
  },
  (err) => {
    const msg = err.response?.data?.message || err.message || 'Network error'
    return Promise.reject(new Error(msg))
  }
)

interface ApiClient {
  get<T>(url: string, config?: AxiosRequestConfig): Promise<T>
  post<T>(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<T>
  put<T>(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<T>
  delete<T>(url: string, config?: AxiosRequestConfig): Promise<T>
}

export default api as unknown as ApiClient
