<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, watch } from 'vue'
import api from '@/services/api'
import { getApiError } from '@/services/apiError'
import { sessionGeneration } from '@/services/authState'
import { enrichmentChangesBody, valueFields } from '@/services/enrichmentSettingsApi'
import type { EnrichmentChanges, EnrichmentSettings, SettingsDraft } from '@/services/enrichmentSettingsApi'
import type { CredentialAction } from '@/services/audioStorageSettingsApi'

const props = defineProps<{
  settings: EnrichmentSettings
  draft: SettingsDraft
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
  for (const [name] of valueFields) {
    if (!name.startsWith('enrichment.tts.') || name.startsWith(`enrichment.tts.${locale === 'uk' ? 'us' : 'uk'}.`)) continue
    if (props.settings.fields[name].editable && props.draft[name] !== props.settings.values[name]) changes[name] = props.draft[name]
  }
  if (props.settings.fields['enrichment.tts.apiKey'].editable) {
    if (props.credential.action === 'clear') changes['enrichment.tts.apiKey'] = { action: 'clear' }
    else if (props.credential.value !== '') changes['enrichment.tts.apiKey'] = { action: 'replace', value: props.credential.value }
  }
  const body = enrichmentChangesBody(changes) as { enrichment?: { tts?: unknown } }
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
  watch(() => JSON.stringify([props.settings.revision, changes(locale), previews[locale].sample]), () => {
    const wasLoading = previews[locale].loading
    stop(locale)
    if (previews[locale].url) previews[locale].stale = true
    if (wasLoading) previews[locale].message = '参数已变化，请重新生成试听。'
  })
}
watch(() => props.disabled, disabled => { if (disabled) for (const locale of locales) stop(locale) })
watch(sessionGeneration, dispose, { flush: 'sync' })
onBeforeUnmount(dispose)
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
      headers: { 'If-Match': props.settings.revision }, responseType: 'blob', timeout: 310000,
      signal: controller.signal, lxIsCurrent: current
    })
    if (!current()) return
    if (!(blob instanceof Blob) || blob.type.split(';')[0] !== 'audio/mpeg' || blob.size < 300 || blob.size > 5 * 1024 * 1024) throw new Error('Invalid preview audio')
    state.url = URL.createObjectURL(blob)
    state.stale = false
    state.message = '试听已生成，点击播放。重复播放不会再次调用语音服务。'
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
  <section aria-labelledby="speech-preview-heading">
    <h3 id="speech-preview-heading">草稿试听</h3>
    <p>使用未保存的语音配置生成 AI 试听，不修改配置或词库。无需开启后台生成、文本服务或音频存储。每次生成会调用语音服务并可能计费。</p>
    <fieldset v-for="locale in locales" :key="locale">
      <legend>{{ locale.toUpperCase() }} 试听</legend>
      <label :for="`speech-sample-${locale}`">{{ locale.toUpperCase() }} 试听样本</label>
      <select :id="`speech-sample-${locale}`" v-model="previews[locale].sample" :disabled="disabled">
        <option value="word">单词：apple</option>
        <option value="sentence">短句：Welcome to our English lesson.</option>
      </select>
      <el-button :disabled="disabled || pending || invalidCredential" :loading="previews[locale].loading" @click="generate(locale)">生成 {{ locale.toUpperCase() }} 试听</el-button>
      <el-button v-if="previews[locale].loading" @click="stop(locale); previews[locale].message = '已取消等待；已经发出的供应商调用仍可能计费。'">取消 {{ locale.toUpperCase() }} 试听</el-button>
      <p role="status" :aria-label="`${locale.toUpperCase()} 试听状态`">{{ previews[locale].message }}</p>
      <p v-if="previews[locale].url && previews[locale].stale">当前试听对应旧参数，请重新生成以听取新效果。</p>
      <audio v-if="previews[locale].url" :ref="el => { players[locale] = el as HTMLAudioElement | undefined }" :src="previews[locale].url" controls preload="none" :aria-label="`${locale.toUpperCase()} 试听播放器`" @play="play(locale)" @error="previews[locale].message = '音频播放失败，请重新生成试听。'" />
    </fieldset>
  </section>
</template>

<style scoped>
fieldset { min-width: 0; margin: var(--lx-space-3) 0; padding: var(--lx-space-3); border: 1px solid var(--lx-color-border); border-radius: var(--lx-radius-sm); }
legend { font-weight: 600; }
label { display: block; margin-bottom: var(--lx-space-2); }
select { display: block; width: 100%; min-height: 36px; margin-bottom: var(--lx-space-3); color: var(--lx-color-text-primary); background: var(--lx-color-bg-surface); }
p { line-height: 1.6; overflow-wrap: anywhere; }
audio { width: 100%; }
</style>
