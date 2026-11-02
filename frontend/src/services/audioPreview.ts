import { reactive } from 'vue'
import { getAdminAudioPlayback, type AudioLocale } from './adminAudioApi'
import { getApiError } from './apiError'
import { sessionGeneration } from './authState'

export function audioTargetKey(wordId: string, meaningId: string | null, locale: AudioLocale) {
  return JSON.stringify([wordId, meaningId, locale])
}

/** One player per drawer; every stop revokes requests, handlers and media references. */
export function createAudioPreview() {
  const state = reactive({ target: '', phase: 'idle' as 'idle' | 'loading' | 'playing', error: '' })
  let generation = 0
  let controller: AbortController | null = null
  let media: HTMLAudioElement | null = null

  function releaseMedia() {
    if (!media) return
    media.onplaying = media.onended = media.onerror = null
    media.pause()
    media.removeAttribute('src')
    media.load()
    media = null
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

  async function play(wordId: string, meaningId: string | null, locale: AudioLocale) {
    const key = audioTargetKey(wordId, meaningId, locale)
    if (state.target === key && state.phase !== 'idle') { stop(); return }
    stop()
    state.target = key
    state.phase = 'loading'
    const current = generation
    const session = sessionGeneration.value
    controller = new AbortController()
    const signal = controller.signal
    const isCurrent = () => current === generation && session === sessionGeneration.value && !signal.aborted

    async function attempt(retry: boolean): Promise<void> {
      if (!isCurrent()) return
      releaseMedia()
      state.phase = 'loading'
      let link
      try {
        link = await getAdminAudioPlayback(wordId, meaningId, locale, { signal, lxIsCurrent: isCurrent })
      } catch (error: unknown) {
        if (!isCurrent()) return
        const status = getApiError(error).status
        state.phase = 'idle'
        state.error = status === 404 ? '音频目标已不存在，请重新打开详情。' : '暂时无法获取音频，请稍后重试。'
        return
      }
      if (!isCurrent()) return
      if (!link || typeof link !== 'object' || link.locale !== locale || !['missing', 'ready', 'stale'].includes(link.status)) {
        state.phase = 'idle'
        state.error = '暂时无法获取音频，请稍后重试。'
        return
      }
      if (link.status === 'missing' || !link.url) {
        state.phase = 'idle'
        state.error = '暂无音频；启用并配置自动补齐后会继续处理适用目标。'
        return
      }
      const expires = Date.parse(link.expiresAt ?? '')
      if (!Number.isFinite(expires) || expires <= Date.now()) {
        if (retry) { await attempt(false); return }
        state.phase = 'idle'
        state.error = '音频链接已过期，请重新点击试听。'
        return
      }
      const audio = new Audio()
      media = audio
      audio.preload = 'none'
      let failed = false
      const fail = async (error?: unknown) => {
        if (failed || !isCurrent() || media !== audio) return
        failed = true
        releaseMedia()
        if (error instanceof DOMException && error.name === 'NotAllowedError') {
          state.phase = 'idle'
          state.error = '浏览器阻止了播放，请重新点击试听并允许音频播放。'
        } else if (retry) {
          await attempt(false)
        } else {
          state.phase = 'idle'
          state.error = '音频播放失败，请检查网络或稍后重试。'
        }
      }
      audio.onplaying = () => { if (isCurrent() && media === audio) state.phase = 'playing' }
      audio.onended = () => {
        if (!isCurrent() || media !== audio) return
        releaseMedia()
        state.phase = 'idle'
      }
      audio.onerror = () => { void fail() }
      audio.src = link.url
      try { await audio.play() } catch (error: unknown) { await fail(error) }
    }
    await attempt(true)
  }

  return { state, play, stop }
}
export type AudioPreview = ReturnType<typeof createAudioPreview>
