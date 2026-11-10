/**
 * Builds the canonical lowercase UUID v4 retry idempotency key.
 *
 * `crypto.randomUUID` is only exposed in secure contexts, while the
 * administration SPA may be served from a plain-HTTP test origin. Fall back to
 * `crypto.getRandomValues` (available there) so the retry action still reaches
 * the server instead of failing silently inside the browser.
 */
export function newRetryKey(): string {
  const cryptoApi = globalThis.crypto
  if (typeof cryptoApi?.randomUUID === 'function') return cryptoApi.randomUUID()
  const bytes = cryptoApi?.getRandomValues
    ? cryptoApi.getRandomValues(new Uint8Array(16))
    : Uint8Array.from({ length: 16 }, () => Math.floor(Math.random() * 256))
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}