import axios from 'axios'

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

export default api
