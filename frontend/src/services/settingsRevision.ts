import api from './api'

/** The quoted 32-hex-digit revision every settings read answers with. */
export function isRevision(value: unknown): value is string {
  return typeof value === 'string' && /^"[0-9a-f]{32}"$/.test(value)
}

/** Sends a settings save with the revision the page read as the `If-Match` the server requires. */
export function putWithRevision(url: string, revision: string, body: unknown, signal: AbortSignal, isCurrent: () => boolean) {
  return api.put<unknown>(url, body, { headers: { 'If-Match': revision }, signal, lxIsCurrent: isCurrent })
}
