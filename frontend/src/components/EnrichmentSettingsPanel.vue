<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { sessionGeneration } from '@/services/authState'
import { getApiError } from '@/services/apiError'
import { readEnrichmentSettings, saveEnrichmentSettings, secretFields, valueFields } from '@/services/enrichmentSettingsApi'
import type { EnrichmentChanges, EnrichmentSettings, SecretField, SettingsDraft, ValueField } from '@/services/enrichmentSettingsApi'
import type { CredentialAction } from '@/services/audioStorageSettingsApi'

const actual = ref<EnrichmentSettings | null>(null)
const draft = reactive({} as SettingsDraft)
const credentials = reactive(Object.fromEntries(secretFields.map(([name]) => [name, { action: 'keep' as CredentialAction, value: '' }])) as Record<SecretField, { action: CredentialAction; value: string }>)
const busy = ref(false), notice = ref('')
const generation = sessionGeneration.value
let active = true, draftReady = false
let controller: AbortController | null = null
const current = () => active && generation === sessionGeneration.value
const sources = { default: '默认配置', file: '持久化文件', environment: '环境变量', commandLine: '启动参数' }
const groups = [
  { key: 'review', title: '审核助手', description: '服务地址和模型均配置时提供审核建议，与补齐文本服务独立；本页不启用自动审核。' },
  { key: 'text', title: '自治补齐与发音准备', description: '字段补齐和音频生成独立，默认关闭。只开音频也必须先完成发音准备，但不写回文本字段。启用后后台自动发现新增和历史缺失，无需选择范围或点开始。' },
  { key: 'tts', title: '语音服务', description: '保存只提交本地配置，不探测或调用供应商。音频存储、文本准备与该侧语音配置就绪后，后台才可生成。' },
  { key: 'uk', title: 'UK 英式发音', description: '自行配置适合英式发音的音色和语速，留空则该侧等待，不影响 US。' },
  { key: 'us', title: 'US 美式发音', description: '自行配置适合美式发音的音色和语速，留空则该侧等待，不影响 UK。' },
  { key: 'policy', title: '后台重试与暂停策略', description: '限流和未调用的等待不扣失败额度；服务恢复不重置已耗尽任务。最大暂停与最大退避不得小于对应初始值。' }
]
const changed = computed(() => actual.value && (valueFields.some(([name]) => actual.value!.fields[name].editable && actual.value!.values[name] !== draft[name])
  || secretFields.some(([name]) => actual.value!.fields[name].editable && (credentials[name].action === 'clear' || credentials[name].value !== ''))))
const invalidReplacement = computed(() => secretFields.some(([name]) => credentials[name].action === 'replace' && !credentials[name].value.trim()))
function clearCredentials() {
  for (const [name] of secretFields) { credentials[name].value = ''; credentials[name].action = 'keep' }
}
function adopt(value: EnrichmentSettings, reset: boolean) {
  actual.value = value
  for (const [name] of valueFields) if (reset || !draftReady || !value.fields[name].editable) draft[name] = value.values[name]
  draftReady = true
}
function begin() {
  controller?.abort()
  controller = new AbortController()
  busy.value = true
  return controller.signal
}
function input(name: ValueField, event: Event, numeric: boolean) {
  const value = (event.target as HTMLInputElement).value
  draft[name] = numeric ? value === '' ? null : Number(value) : value
}
function display(value: unknown) { return value === null || value === '' ? '（空）' : value === true ? '开启' : value === false ? '关闭' : String(value) }
function localeStatus(locale: 'uk' | 'us') {
  const state = actual.value
  if (!state) return '未知'
  const v = state.values
  if (!v['enrichment.audioEnabled']) return '等待：音频生成关闭'
  if (!state.configurationReady) return '等待：持久配置版本未就绪'
  if (!v['enrichment.text.endpoint'] || !v['enrichment.text.model'] || !state.hasApiKey['enrichment.text.apiKey']) return '等待：文本准备配置缺失'
  if (!v['enrichment.tts.endpoint'] || !v['enrichment.tts.model'] || !state.hasApiKey['enrichment.tts.apiKey'] || !v[`enrichment.tts.${locale}.voice`] || v[`enrichment.tts.${locale}.speed`] === null) return '等待：语音服务或该侧音色/语速缺失'
  return '本地必填配置齐全；供应商能力、存储及任务结果需另行核对'
}
async function load() {
  if (!current() || busy.value) return
  const signal = begin()
  clearCredentials()
  try {
    const value = await readEnrichmentSettings(signal, current)
    if (current()) { adopt(value, false); notice.value = '已读取当前配置，请核对非秘密草稿后保存。' }
  } catch {
    if (current()) { actual.value = null; notice.value = '配置状态未知：读取失败，请重新读取。' }
  } finally { if (current()) busy.value = false }
}
async function save() {
  if (!current() || busy.value || !actual.value || !changed.value || invalidReplacement.value) return
  const previous = actual.value, changes: EnrichmentChanges = {}
  for (const [name] of valueFields) if (previous.fields[name].editable && previous.values[name] !== draft[name]) changes[name] = draft[name]
  for (const [name] of secretFields) if (previous.fields[name].editable) {
    const credential = credentials[name]
    changes[name] = credential.action === 'clear' ? { action: 'clear' }
      : credential.value !== '' ? { action: 'replace', value: credential.value } : { action: 'keep' }
  }
  const signal = begin()
  clearCredentials()
  notice.value = '正在保存，等待服务器确认。'
  try {
    const value = await saveEnrichmentSettings(previous.revision, changes, signal, current)
    if (current()) { adopt(value, true); notice.value = '已保存并发布当前配置，密钥输入已清空。保存不代表供应商可用或音频完成。' }
  } catch (error: unknown) {
    if (!current()) return
    const status = getApiError(error).status
    if (status === undefined) {
      actual.value = null
      notice.value = '保存结果未知，正在回读；不会自动重复提交密钥。'
      try {
        const value = await readEnrichmentSettings(signal, current)
        if (current()) { adopt(value, false); notice.value = '保存响应未确认，已回读当前配置；请核对草稿，所需密钥须重新填写。' }
      } catch { if (current()) notice.value = '保存结果未知，回读失败；请重新读取。密钥输入已清空。' }
    } else {
      const messages: Record<number, string> = {
        400: '配置或版本格式无效，请检查字段范围和服务地址。',
        409: '字段被部署配置覆盖，请重新读取来源与只读原因。',
        412: '共享设置版本已变化，请重新读取并核对，不能覆盖其他面板或管理员的更新。',
        413: '配置请求超过服务器大小限制，请缩短输入。',
        428: '缺少设置版本，请重新读取后保存。',
        503: '配置文件不可写或保存服务不可用，原配置保留。请检查文件权限后重试。'
      }
      notice.value = `保存失败（${status}）：${messages[status] ?? '请求未被接受，请重新读取并检查配置。'} 非秘密草稿已保留，密钥输入已清空。`
      if ([409, 412, 428].includes(status)) actual.value = null
    }
  } finally { if (current()) busy.value = false }
}
function cancel() {
  active = false
  controller?.abort()
  clearCredentials()
  actual.value = null
  for (const [name] of valueFields) delete (draft as Partial<SettingsDraft>)[name]
}
watch(sessionGeneration, cancel, { flush: 'sync' })
onBeforeUnmount(cancel)
onMounted(() => { void load() })
</script>

<template>
  <el-card class="enrichment-settings" shadow="never">
    <section aria-labelledby="enrichment-settings-heading">
      <h2 id="enrichment-settings-heading">审核与自治补齐</h2>
      <p role="status" aria-live="polite" aria-label="审核与补齐配置状态">{{ notice || '正在读取当前配置…' }}</p>
      <p v-if="!actual">审核与补齐当前配置：未知</p>
      <form v-if="actual" @submit.prevent="save">
        <p>持久配置版本：{{ actual.configurationReady ? '已就绪' : '等待建立' }}；这不是供应商可用性或生成完成状态。</p>
        <p aria-label="UK 配置状态">UK 当前配置：{{ localeStatus('uk') }}</p>
        <p aria-label="US 配置状态">US 当前配置：{{ localeStatus('us') }}</p>
        <fieldset v-for="group in groups" :key="group.key">
          <legend>{{ group.title }}</legend>
          <p>{{ group.description }}</p>
          <div v-for="field in valueFields.filter(item => item[2] === group.key)" :key="field[0]" class="enrichment-field">
            <label :for="`enrichment-${field[0]}`">{{ field[1] }}</label>
            <input v-if="field[3] === 'boolean'" :id="`enrichment-${field[0]}`" v-model="draft[field[0]]" type="checkbox" :disabled="busy || !actual.fields[field[0]].editable" :aria-describedby="`source-${field[0]}`">
            <select v-else-if="field[3] === 'provider'" :id="`enrichment-${field[0]}`" v-model="draft[field[0]]" :disabled="busy || !actual.fields[field[0]].editable" :aria-describedby="`source-${field[0]}`">
              <option value="MiniMaxNative">MiniMaxNative</option><option value="OpenAICompatible">OpenAICompatible</option>
            </select>
            <input v-else :id="`enrichment-${field[0]}`" :value="draft[field[0]] ?? ''" :type="field[3] === 'text' ? 'text' : 'number'" :min="field[4]" :max="field[5]" :step="field[3] === 'speed' ? 'any' : 1" :required="field[3] === 'integer'" :maxlength="2048" :disabled="busy || !actual.fields[field[0]].editable" :aria-describedby="`source-${field[0]}`" autocomplete="off" spellcheck="false" @input="input(field[0], $event, field[3] !== 'text')">
            <p :id="`source-${field[0]}`">当前生效：{{ display(actual.values[field[0]]) }} · {{ sources[actual.fields[field[0]].source] }}<span v-if="!actual.fields[field[0]].editable"> · 网页只读：{{ actual.fields[field[0]].reason }}</span></p>
          </div>
          <div v-for="[name, label] in secretFields.filter(item => item[2] === group.key)" :key="name" class="enrichment-field">
            <label :for="`enrichment-${name}`">{{ label }}</label>
            <p :id="`source-${name}`">{{ actual.hasApiKey[name] ? '已有密钥（不返回原值）' : '未设置密钥' }} · {{ sources[actual.fields[name].source] }}<span v-if="!actual.fields[name].editable"> · 网页只读：{{ actual.fields[name].reason }}</span></p>
            <select v-model="credentials[name].action" :aria-label="`${label}操作`" :disabled="busy || !actual.fields[name].editable" @change="credentials[name].value = ''">
              <option value="keep">保留；填写新值即替换</option><option value="replace">替换为新值</option><option value="clear">显式清除</option>
            </select>
            <input :id="`enrichment-${name}`" v-model="credentials[name].value" type="password" :maxlength="4096" :disabled="busy || !actual.fields[name].editable || credentials[name].action === 'clear'" :aria-describedby="`source-${name}`" autocomplete="new-password" spellcheck="false">
          </div>
        </fieldset>
        <aside aria-label="语音能力限制">
          <h3>语音能力限制</h3>
          <p>MiniMaxNative：仅 https://api.minimax.io/v1 或 https://api.minimax.cn/v1，speech-2.8-hd / speech-2.8-turbo。要求覆盖完整朗读目标的 IPA，不接受自由指令；语速 0.5–2。</p>
          <p>OpenAICompatible：仅 https://api.openai.com/v1，gpt-4o-mini-tts / gpt-4o-mini-tts-2025-12-15 支持独立 instructions 表达口音及 IPA；语速 0.25–4。tts-1 / tts-1-hd 仅纯文本，准备结果含 IPA 或提示时安全停止。</p>
          <p>兼容协议不代表任意模型支持指定读音。其他组合或无法表达的读音要求会安全停止，不发送忽略约束的请求。音色需自行确认供应商支持与实际口音；本页不推荐或预填音色、语速。</p>
          <p>配置允许保存 0.1–4 的语速，实际生成仍受上述更窄范围限制。能力声明不代替真实试听。<a href="https://github.com/philfanzhou/Lexarbor/blob/main/docs/api/Audio.md#speech-adapters" target="_blank" rel="noopener noreferrer">查看英文语音能力矩阵</a></p>
        </aside>
        <p>密钥空白默认保留，清除须显式选择；每次提交、重新读取、离开页面或开始登出均清空密钥输入。错误不会回显输入。非秘密草稿在失败后保留，重新读取后须核对再保存。</p>
        <el-button type="primary" native-type="submit" :disabled="busy || !changed || invalidReplacement" :loading="busy">保存审核与补齐设置</el-button>
      </form>
      <el-button class="reload-enrichment" :disabled="busy" @click="load">重新读取审核与补齐</el-button>
    </section>
  </el-card>
</template>

<style scoped>
.enrichment-settings { margin-top: var(--lx-space-4); }
h2 { margin-top: 0; }
p { overflow-wrap: anywhere; line-height: 1.6; }
fieldset { min-width: 0; margin: var(--lx-space-4) 0; padding: var(--lx-space-3); border: 1px solid var(--lx-color-border); border-radius: var(--lx-radius-sm); }
legend { font-weight: 600; }
.enrichment-field { padding: var(--lx-space-3) 0; border-top: 1px solid var(--lx-color-border-light); }
.enrichment-field > label { display: block; margin-bottom: var(--lx-space-2); }
.enrichment-field input:not([type='checkbox']), select { width: 100%; box-sizing: border-box; min-height: 36px; padding: var(--lx-space-2); border: 1px solid var(--lx-color-border); border-radius: var(--lx-radius-sm); color: var(--lx-color-text-primary); background: var(--lx-color-bg-surface); }
.enrichment-field input:disabled, select:disabled { opacity: .7; }
select { margin-bottom: var(--lx-space-2); }
.reload-enrichment { margin: var(--lx-space-3) 0 0; }
</style>
