import api from './api'

export interface AdminSession {
  username: string
  roles: string[]
}

export function createSession(username: string, password: string) {
  return api.post<AdminSession>('/admin/auth/login', { username, password })
}

export function getSession() {
  return api.get<AdminSession>('/admin/auth/session')
}

export function deleteSession() {
  return api.post<void>('/admin/auth/logout')
}
