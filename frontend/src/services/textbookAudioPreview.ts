import { reactive } from 'vue'
import { getTextbookAudioPlayback, type TextbookAudioPlayback } from './textbookAudioApi'
import { getApiError } from './apiError'
import { sessionGeneration } from './authState'

export function textbookAudioTargetKey(bookId: string, unitId: string, trackId: string) {
  return JSON.stringify([bookId, unitId, trackId])
}

/** Validate the actual three-field response before any URL reaches a media element. */
export function readTextbookAudioPlayback(value: unknown, trackId: string): TextbookAudioPlayback {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid playback response')
  const data = value as Record<string, unknown>
  if (Object.keys(data).sort().join(',') !== 'expiresAt,trackId,url' || data.trackId !== trackId
    || typeof data.url !== 'string' || typeof data.expiresAt !== 'string'
    || !/(Z|\+00:00)$/.test(data.expiresAt) || !Number.isFinite(Date.parse(data.expiresAt))) throw new Error('Invalid playback response')
  const url = new URL(data.url)
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new Error('Invalid playback response')
  return data as unknown as TextbookAudioPlayback
}

/** One page-scoped player. Every callback owns both its generation and media. */
export function createTextbookAudioPreview() {
  const state = reactive({ target: '', phase: 'idle' as 'idle' | 'loading' | 'playing', error: '' })
  let generation = 0
  let controller: AbortController | null = null
  let media: HTMLAudioElement | null = null
  function releaseMedia() {
    const audio = media
    media = null
    if (!audio) return
    audio.onplaying = audio.onended = audio.onerror = null
    audio.pause()
    audio.removeAttribute('src')
    audio.load()
  }
  function stop() {
    generation += 1
    controller?.abort()
    controller = null
    releaseMedia()
    state.target = ''
    state.phase = 'idle'
    state.error = ''
  }
  async function play(bookId: string, unitId: string, trackId: string) {
    const target = textbookAudioTargetKey(bookId, unitId, trackId)
    if (state.target === target && state.phase !== 'idle') { stop(); return }
    stop()
    state.target = target
    state.phase = 'loading'
    const own = generation
    const session = sessionGeneration.value
    controller = new AbortController()
    const signal = controller.signal
    const current = () => own === generation && session === sessionGeneration.value && !signal.aborted
    async function attempt(renew: boolean): Promise<void> {
      if (!current()) return
      releaseMedia()
      state.phase = 'loading'
      let link: TextbookAudioPlayback
      try {
        const value = await getTextbookAudioPlayback(bookId, unitId, trackId, { signal, lxIsCurrent: current })
        if (!current()) return
        link = readTextbookAudioPlayback(value, trackId)
      } catch (error: unknown) {
        if (!current()) return
        state.phase = 'idle'
        state.error = getApiError(error).status === 404 ? '录音或文件已不存在，请重新读取目录。' : '暂时无法获取试听，音频可能已关闭或存储不可用；请稍后重试。'
        return
      }
      if (Date.parse(link.expiresAt) <= Date.now()) {
        link.url = ''
        if (renew) { await attempt(false); return }
        state.phase = 'idle'
        state.error = '试听链接已过期，请重新点击试听。'
        return
      }
      const audio = new Audio()
      media = audio
      audio.preload = 'none'
      let failed = false
      const fail = async (error?: unknown) => {
        if (failed || !current() || media !== audio) return
        failed = true
        releaseMedia()
        if (error instanceof DOMException && error.name === 'NotAllowedError') {
          state.phase = 'idle'
          state.error = '浏览器阻止了播放，请重新点击试听并允许音频播放。'
        } else if (renew) await attempt(false)
        else {
          state.phase = 'idle'
          state.error = '录音播放失败，请检查网络或稍后重新点击试听。'
        }
      }
      audio.onplaying = () => { if (current() && media === audio) state.phase = 'playing' }
      audio.onended = () => {
        if (!current() || media !== audio) return
        releaseMedia()
        state.phase = 'idle'
      }
      audio.onerror = () => { void fail() }
      audio.src = link.url
      // A play promise may remain unsettled after stop. Its async frame must
      // not keep the signature alive after media has been released.
      link.url = ''
      try { await audio.play() } catch (error: unknown) { await fail(error) }
    }
    await attempt(true)
  }
  return { state, play, stop }
}
