<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { getAdminAudio, type AudioMetadata, type AudioLocale } from '@/services/adminAudioApi'
import { audioTargetKey, type AudioPreview } from '@/services/audioPreview'
import { isAuthenticated, sessionGeneration } from '@/services/authState'

const props = defineProps<{ wordId: string; meaningId: string | null; label: string; player: AudioPreview }>()
const metadata = ref<AudioMetadata | null>(null)
const loading = ref(true)
const unavailable = ref(false)
let generation = 0
let controller: AbortController | null = null
function load() {
  generation += 1
  const current = generation
  const session = sessionGeneration.value
  controller?.abort()
  controller = new AbortController()
  const signal = controller.signal
  const isCurrent = () => current === generation && session === sessionGeneration.value && !signal.aborted
  metadata.value = null
  loading.value = true
  unavailable.value = false
  if (!isAuthenticated.value) { loading.value = false; unavailable.value = true; return }
  getAdminAudio(props.wordId, props.meaningId, { signal, lxIsCurrent: isCurrent }).then(
    data => {
      if (!isCurrent()) return
      if (!data || typeof data !== 'object' || typeof data.enabled !== 'boolean' || !Array.isArray(data.items) || data.items.length !== 2 ||
          !['en-GB', 'en-US'].every(locale => data.items.some(item => item && typeof item === 'object' && item.locale === locale &&
            item.wordId === props.wordId && item.meaningId === props.meaningId &&
            ['missing', 'ready', 'stale'].includes(item.status)))) {
        unavailable.value = true
      } else metadata.value = data
      loading.value = false
    },
    () => { if (isCurrent()) { loading.value = false; unavailable.value = true } }
  )
}
watch(() => [props.wordId, props.meaningId], load, { immediate: true, flush: 'sync' })
watch(sessionGeneration, () => {
  generation += 1
  controller?.abort()
  metadata.value = null
  loading.value = false
  unavailable.value = true
}, { flush: 'sync' })
onBeforeUnmount(() => { generation += 1; controller?.abort() })
function key(locale: AudioLocale) { return audioTargetKey(props.wordId, props.meaningId, locale) }
function accent(locale: AudioLocale) { return locale === 'en-GB' ? '英式' : '美式' }
</script>

<template>
  <section class="audio-preview" :aria-label="`${label}音频`" aria-live="polite">
    <p v-if="loading">音频状态加载中…</p>
    <p v-else-if="unavailable">音频暂不可用（服务未提供音频接口或请求失败）。<el-button link @click="load">重试音频状态</el-button></p>
    <template v-else-if="metadata">
      <p v-if="!metadata.enabled">音频功能未启用，暂不可试听。</p>
      <div v-for="item in metadata.items" :key="item.locale" class="audio-preview__row">
        <span>{{ accent(item.locale) }}：{{ item.status === 'missing' ? '缺失' : item.status === 'stale' ? '需更新（仍可试听）' : '可用' }}</span>
        <span v-if="item.effectiveBinding">{{ item.source === 'meaning' ? '释义覆盖' : meaningId ? '使用单词默认' : '单词默认' }}</span>
        <span v-else>无音频来源</span>
        <el-button
          size="small" :disabled="!metadata.enabled || item.status === 'missing'"
          :aria-label="`${player.state.target === key(item.locale) && player.state.phase !== 'idle' ? '停止' : '试听'}${label}${accent(item.locale)}音频`"
          @click="player.play(wordId, meaningId, item.locale)"
        >
          {{ player.state.target === key(item.locale) && player.state.phase !== 'idle' ? '停止' : '试听' }}
        </el-button>
        <span v-if="player.state.target === key(item.locale) && player.state.phase === 'loading'">正在获取/加载音频…</span>
        <span v-if="player.state.target === key(item.locale) && player.state.phase === 'playing'">正在播放</span>
        <p v-if="player.state.target === key(item.locale) && player.state.error" role="alert">{{ player.state.error }}</p>
      </div>
    </template>
  </section>
</template>

<style scoped>
.audio-preview { margin-top: var(--lx-space-2); font-size: var(--lx-font-size-xs); color: var(--lx-color-text-secondary); width: 100%; }
.audio-preview p { margin: var(--lx-space-1) 0; }
.audio-preview__row { display: flex; flex-wrap: wrap; align-items: center; gap: var(--lx-space-2); margin-top: var(--lx-space-1); }
.audio-preview__row p { width: 100%; color: var(--lx-color-danger); }
</style>
