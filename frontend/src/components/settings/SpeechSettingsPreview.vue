<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, watch } from 'vue'
import api, { modelRequestTimeoutMs } from '@/services/api'
import { getApiError } from '@/services/apiError'
import { sessionGeneration } from '@/services/authState'
import type { CredentialAction } from '@/services/audioStorageSettingsApi'
import { enrichmentChangesBody } from '@/services/enrichmentSettingsApi'
import type { EnrichmentChanges } from '@/services/enrichmentSettingsApi'
import type { SettingValue } from '@/services/settingsCatalogApi'

// A draft preview through the server's own speech adapter: the browser never calls a supplier.
const props = defineProps<{
  revision: string
  /** Saved and draft values of the speech fields, keyed by the module's dotted names. */
  saved: Record<string, SettingValue>
  draft: Record<string, SettingValue>
  editable: Record<string, boolean>
  credential: { action: CredentialAction; value: string }
  disabled: boolean
}>()
type Locale = 'uk' | 'us'
const locales: Locale[] = ['uk', 'us']
const previews = reactive({ uk: state(), us: state() })
function state() { return { sample: 'word', url: '', stale: false, loading: false, message: '' } }
const players: Partial<Record<Locale, HTMLAudioElement>> = {}
const controllers: Partial<Record<Locale, AbortController>> = {}
const generations = { uk: 0, us: 0 }
const session = sessionGeneration.value
let active = true
const pending = computed(() => previews.uk.loading || previews.us.loading)
const invalidCredential = computed(() => props.credential.action === 'replace' && !props.credential.value.trim())
function changes(locale: Locale) {
  const changes: EnrichmentChanges = {}
  const patch = changes as Record<string, unknown>
  for (const [name, value] of Object.entries(props.draft)) {
    if (!name.startsWith('enrichment.tts.') || name.startsWith(`enrichment.tts.${locale === 'uk' ? 'us' : 'uk'}.`)) continue
    if (props.editable[name] && value !== props.saved[name]) patch[name] = value
  }
  if (props.editable['enrichment.tts.apiKey']) {
    if (props.credential.action === 'clear') patch['enrichment.tts.apiKey'] = { action: 'clear' }
    else if (props.credential.value !== '') patch['enrichment.tts.apiKey'] = { action: 'replace', value: props.credential.value }
  }
  const body = enrichmentChangesBody(changes) as { enrichment?: { tts?: Record<string, unknown> } }
  return body.enrichment?.tts ?? {}
}
function stop(locale: Locale) {
  generations[locale]++
  controllers[locale]?.abort()
  delete controllers[locale]
  previews[locale].loading = false
  players[locale]?.pause()
}
function clear(locale: Locale) {
  stop(locale)
  if (previews[locale].url) URL.revokeObjectURL(previews[locale].url)
  previews[locale].url = ''
}
function dispose() {
  active = false
  for (const locale of locales) clear(locale)
}
for (const locale of locales) {
  watch(() => JSON.stringify([props.revision, changes(locale), previews[locale].sample]), () => {
    const wasLoading = previews[locale].loading
    stop(locale)
    if (previews[locale].url) previews[locale].stale = true
    if (wasLoading) previews[locale].message = '参数已变化，请重新生成试听。'
  })
}
watch(() => props.disabled, disabled => { if (disabled) for (const locale of locales) stop(locale) })
watch(sessionGeneration, dispose, { flush: 'sync' })
onBeforeUnmount(dispose)
function cancel(locale: Locale) {
  stop(locale)
  previews[locale].message = '已取消等待；已经发出的供应商调用仍可能计费。'
}
async function generate(locale: Locale) {
  if (!active || props.disabled || pending.value || invalidCredential.value) return
  const state = previews[locale]
  clear(locale)
  const generation = generations[locale]
  const controller = new AbortController()
  controllers[locale] = controller
  const current = () => active && session === sessionGeneration.value && generation === generations[locale] && !controller.signal.aborted
  state.loading = true
  state.message = '正在使用草稿生成试听…'
  try {
    const blob = await api.post<Blob>('/admin/enrichment/settings/speech-preview', { locale, sample: state.sample, tts: changes(locale) }, {
      headers: { 'If-Match': props.revision }, responseType: 'blob', timeout: modelRequestTimeoutMs,
      signal: controller.signal, lxIsCurrent: current
    })
    if (!current()) return
    if (!(blob instanceof Blob) || blob.type.split(';')[0] !== 'audio/mpeg' || blob.size < 300 || blob.size > 5 * 1024 * 1024) throw new Error('Invalid preview audio')
    state.url = URL.createObjectURL(blob)
    state.stale = false
    state.message = '试听已生成，点击播放。'
  } catch (error: unknown) {
    if (!current()) return
    const status = getApiError(error).status
    const messages: Record<number, string> = {
      400: '无法生成：请检查服务地址、模型、密钥、音色和参数是否受支持。',
      409: '配置被部署设置覆盖，请重新读取后试听。',
      412: '配置版本已变化，请重新读取并核对草稿。',
      413: '试听请求过大，请缩短输入。',
      428: '缺少配置版本，请重新读取。',
      429: '试听繁忙或请求过于频繁，请稍后重试。',
      502: '语音服务暂时失败或返回无效音频，请稍后重试。'
    }
    state.message = status ? messages[status] ?? '试听失败，请检查登录状态或稍后重试。' : '未收到试听结果，请稍后重试。'
  } finally {
    if (current()) { state.loading = false; delete controllers[locale] }
  }
}
function play(locale: Locale) {
  for (const other of locales) if (other !== locale) players[other]?.pause()
}
</script>

<template>
  <div class="lx-preview">
    <fieldset v-for="locale in locales" :key="locale" class="lx-preview__locale">
      <legend>{{ locale === 'uk' ? '英式' : '美式' }}（{{ locale.toUpperCase() }}）</legend>
      <div class="lx-preview__row">
        <label :for="`speech-sample-${locale}`">{{ locale.toUpperCase() }} 试听样本</label>
        <select :id="`speech-sample-${locale}`" v-model="previews[locale].sample" class="lx-select lx-select--inline" :disabled="disabled">
          <option value="word">单词：apple</option>
          <option value="sentence">短句：Welcome to our English lesson.</option>
        </select>
        <el-button
          type="primary"
          plain
          :disabled="disabled || pending || invalidCredential"
          :loading="previews[locale].loading"
          @click="generate(locale)"
        >
          生成 {{ locale.toUpperCase() }} 试听
        </el-button>
        <el-button v-if="previews[locale].loading" @click="cancel(locale)">取消 {{ locale.toUpperCase() }} 试听</el-button>
      </div>
      <p role="status" :aria-label="`${locale.toUpperCase()} 试听状态`" class="lx-setting__meta">{{ previews[locale].message }}</p>
      <p v-if="previews[locale].url && previews[locale].stale" class="lx-setting__meta">当前试听对应旧参数，请重新生成以听取新效果。</p>
      <audio
        v-if="previews[locale].url"
        :ref="element => { players[locale] = element as HTMLAudioElement | undefined }"
        :src="previews[locale].url"
        controls
        preload="none"
        :aria-label="`${locale.toUpperCase()} 试听播放器`"
        @play="play(locale)"
        @error="previews[locale].message = '音频播放失败，请重新生成试听。'"
      />
    </fieldset>
    <p class="lx-setting__meta">生成的音频只保存在当前页面；重复播放不会再次调用语音服务。</p>
  </div>
</template>

<style scoped>
.lx-preview { display: flex; flex-direction: column; gap: var(--lx-space-3); }
.lx-preview__locale { min-width: 0; margin: 0; padding: var(--lx-space-3) var(--lx-space-4); border: 1px solid var(--lx-color-border-light); border-radius: var(--lx-radius-md); background: var(--lx-color-bg-page); }
.lx-preview__locale legend { padding: 0 var(--lx-space-2); color: var(--lx-color-text-primary); font-size: var(--lx-font-size-sm); font-weight: 600; }
.lx-preview__row { display: flex; flex-wrap: wrap; align-items: center; gap: var(--lx-space-2); }
.lx-preview__row label { color: var(--lx-color-text-regular); font-size: var(--lx-font-size-sm); }
/* The sample choice may shrink on a phone instead of stretching the page. */
.lx-preview__row .lx-select { flex: 1 1 160px; min-width: 0; max-width: 100%; }
.lx-preview__row .el-button + .el-button { margin-left: 0; }
.lx-preview__row .el-button { flex: none; margin-left: var(--lx-space-2); }
audio { width: 100%; max-width: 520px; display: block; margin-top: var(--lx-space-2); }
</style>
