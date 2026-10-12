<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { sessionGeneration } from '@/services/authState'
import { getApiError } from '@/services/apiError'
import { audioTextFields, credentialFields, readAudioStorage, saveAudioStorage } from '@/services/audioStorageSettingsApi'
import type { AudioStorageChanges, AudioStorageSettings, CredentialAction } from '@/services/audioStorageSettingsApi'

const actual = ref<AudioStorageSettings | null>(null)
const draft = reactive({ enabled: false, internalEndpoint: '', publicEndpoint: '', region: '', bucket: '', keyPrefix: '' })
const credentials = reactive({ accessKey: { action: 'keep' as CredentialAction, value: '' }, secretKey: { action: 'keep' as CredentialAction, value: '' } })
const busy = ref(false)
const notice = ref('')
const generation = sessionGeneration.value
let active = true
let draftReady = false
let controller: AbortController | null = null
const current = () => active && generation === sessionGeneration.value
const labels = { internalEndpoint: '服务访问地址 InternalEndpoint', publicEndpoint: '浏览器播放地址 PublicEndpoint', region: '区域 Region', bucket: '私有桶 Bucket', keyPrefix: '对象前缀 KeyPrefix', accessKey: 'AccessKey', secretKey: 'SecretKey' }
const sources = { default: '默认配置', file: '持久化文件', environment: '环境变量', commandLine: '启动参数' }
const changed = computed(() => actual.value && (['enabled', ...audioTextFields] as const).some(key => actual.value!.fields[key].editable && actual.value![key] !== draft[key])
  || actual.value && credentialFields.some(key => actual.value!.fields[key].editable && (credentials[key].action === 'clear' || credentials[key].value !== '')))
const invalidReplacement = computed(() => credentialFields.some(key => credentials[key].action === 'replace' && !credentials[key].value.trim()))
function clearCredentials() {
  for (const key of credentialFields) { credentials[key].value = ''; credentials[key].action = 'keep' }
}
function adopt(value: AudioStorageSettings, resetDraft: boolean) {
  actual.value = value
  for (const key of ['enabled', ...audioTextFields] as const) {
    if (resetDraft || !draftReady || !value.fields[key].editable) {
      if (key === 'enabled') draft.enabled = value.enabled
      else draft[key] = value[key]
    }
  }
  draftReady = true
}
function begin() {
  controller?.abort()
  controller = new AbortController()
  busy.value = true
  return controller.signal
}
async function load() {
  if (!current() || busy.value) return
  const signal = begin()
  clearCredentials()
  try {
    const value = await readAudioStorage(signal, current)
    if (current()) { adopt(value, false); notice.value = '已读取当前生效配置，请核对草稿后保存。' }
  } catch {
    if (current()) { actual.value = null; notice.value = '配置状态未知：读取失败，请重试。' }
  } finally { if (current()) busy.value = false }
}
async function save() {
  if (!current() || busy.value || !actual.value || !changed.value || invalidReplacement.value) return
  const previous = actual.value
  const changes: AudioStorageChanges = {}
  if (previous.fields.enabled.editable && previous.enabled !== draft.enabled) changes.enabled = draft.enabled
  for (const key of audioTextFields) if (previous.fields[key].editable && previous[key] !== draft[key]) changes[key] = draft[key]
  for (const key of credentialFields) if (previous.fields[key].editable) {
    const credential = credentials[key]
    changes[key] = credential.action === 'clear' ? { action: 'clear' }
      : credential.value !== '' ? { action: 'replace', value: credential.value } : { action: 'keep' }
  }
  const signal = begin()
  notice.value = '正在保存，等待服务器确认。'
  // A credential-bearing attempt is never retained as a retryable component draft.
  clearCredentials()
  try {
    const value = await saveAudioStorage(previous.revision, changes, signal, current)
    if (current()) { adopt(value, true); notice.value = '已保存到配置文件并立即生效，密钥输入已清空。' }
  } catch (error: unknown) {
    if (!current()) return
    const status = getApiError(error).status
    if (status === undefined) {
      actual.value = null
      notice.value = '保存结果未知，正在回读；不会自动重复提交密钥。'
      try {
        const value = await readAudioStorage(signal, current)
        if (current()) { adopt(value, false); notice.value = '保存响应未确认，已回读当前生效配置；请核对后重新填写所需密钥。' }
      } catch { if (current()) notice.value = '保存结果未知，回读失败；请重新读取。密钥输入已清空。' }
    } else {
      // Fixed local messages cannot accidentally echo credential input from an error body.
      const messages: Record<number, string> = {
        400: '配置无效，请检查启用时的完整字段和密钥；版本格式错误也需要重新读取。',
        409: '部署覆盖或已有音频、正在运行的操作阻止修改。存储目的地需要单独迁移，请重新读取。',
        412: '设置版本已变化，请重新读取后核对草稿再保存，不能覆盖其他管理员的更新。',
        428: '缺少设置版本，请重新读取后再保存。',
        503: '配置文件不可写或保存服务不可用，原配置保留。请检查文件权限后重试。'
      }
      notice.value = `保存失败（${status}）：${messages[status] ?? '请求未被服务器接受，请重新读取并检查配置。'} 密钥输入已清空。`
      if ([409, 412, 428].includes(status)) actual.value = null
    }
  } finally { if (current()) busy.value = false }
}
function cancel() {
  active = false
  controller?.abort()
  clearCredentials()
  actual.value = null
  for (const key of audioTextFields) draft[key] = ''
  draft.enabled = false
}
watch(sessionGeneration, cancel, { flush: 'sync' })
onBeforeUnmount(cancel)
onMounted(() => { void load() })
</script>

<template>
  <el-card class="audio-settings" shadow="never">
    <section aria-labelledby="audio-settings-heading">
      <h2 id="audio-settings-heading">音频存储（S3）</h2>
      <p>保存会同步配置文件并立即生效；文件不存在时自动创建，刷新或重启后保留。启用只表示本地配置有效，连接和权限请用实际上传、试听验证。</p>
      <p role="status" aria-live="polite" aria-label="音频存储状态">{{ notice || '正在读取当前生效配置…' }}</p>
      <p v-if="!actual">音频存储当前生效状态：未知</p>
      <form v-if="actual" @submit.prevent="save">
        <p>当前生效：<strong>{{ actual.enabled ? '开启' : '关闭' }}</strong></p>
        <div class="audio-field">
          <label><input v-model="draft.enabled" type="checkbox" :disabled="busy || !actual.fields.enabled.editable" aria-label="音频存储草稿开关"> 启用音频存储</label>
          <p>{{ sources[actual.fields.enabled.source] }}<span v-if="!actual.fields.enabled.editable">覆盖，网页只读；请修改部署配置。</span></p>
        </div>
        <div v-for="key in audioTextFields" :key="key" class="audio-field">
          <label :for="`audio-${key}`">{{ labels[key] }}</label>
          <input :id="`audio-${key}`" v-model="draft[key]" type="text" :disabled="busy || !actual.fields[key].editable" :aria-describedby="`audio-source-${key}`" autocomplete="off" spellcheck="false">
          <p :id="`audio-source-${key}`">当前生效：{{ actual[key] || '（空）' }} · {{ sources[actual.fields[key].source] }}<span v-if="!actual.fields[key].editable">覆盖，网页只读；请修改部署配置。</span></p>
        </div>
        <div v-for="key in credentialFields" :key="key" class="audio-field">
          <label :for="`audio-${key}`">{{ labels[key] }}</label>
          <p :id="`audio-source-${key}`">{{ (key === 'accessKey' ? actual.hasAccessKey : actual.hasSecretKey) ? '已有密钥（不返回原值）' : '未设置密钥' }} · {{ sources[actual.fields[key].source] }}<span v-if="!actual.fields[key].editable">覆盖，网页只读；请修改部署配置。</span></p>
          <select v-model="credentials[key].action" :aria-label="`${key}操作`" :disabled="busy || !actual.fields[key].editable" @change="credentials[key].value = ''">
            <option value="keep">保留；填写新值即替换</option><option value="replace">替换为新值</option><option value="clear">显式清除</option>
          </select>
          <input :id="`audio-${key}`" v-model="credentials[key].value" type="password" :disabled="busy || !actual.fields[key].editable || credentials[key].action === 'clear'" :aria-describedby="`audio-source-${key}`" autocomplete="new-password" spellcheck="false">
        </div>
        <p>密钥默认空白表示保留，清除需显式选择。关闭不会清除密钥或删除对象；关闭时可保存未完成的配置。离开、登出或提交后清空密钥输入。</p>
        <p>InternalEndpoint、Bucket、KeyPrefix 是存储目的地；已有历史音频或运行中的操作时，服务器拒绝修改（409），需单独迁移。</p>
        <p>高级上传、排队、超时、恢复和签名有效期参数保持原配置，本页只作说明；实际值请查看服务器配置文件，修改后重启。</p>
        <el-button type="primary" native-type="submit" :disabled="busy || !changed || invalidReplacement" :loading="busy">保存音频存储设置</el-button>
      </form>
      <el-button class="reload-audio" :disabled="busy" @click="load">重新读取音频存储</el-button>
    </section>
  </el-card>
</template>

<style scoped>
.audio-settings { margin-top: var(--lx-space-4); }
h2 { margin-top: 0; }
p { overflow-wrap: anywhere; line-height: 1.6; }
.audio-field { padding: var(--lx-space-3) 0; border-top: 1px solid var(--lx-color-border-light); }
.audio-field > label { display: block; margin-bottom: var(--lx-space-2); }
.audio-field input[type='text'], .audio-field input[type='password'], .audio-field select { width: 100%; box-sizing: border-box; min-height: 36px; padding: var(--lx-space-2); border: 1px solid var(--lx-color-border); border-radius: var(--lx-radius-sm); color: var(--lx-color-text-primary); background: var(--lx-color-bg-surface); }
.audio-field input:disabled, .audio-field select:disabled { opacity: .7; }
.audio-field select { margin-bottom: var(--lx-space-2); }
.reload-audio { margin: var(--lx-space-3) 0 0; }
</style>
