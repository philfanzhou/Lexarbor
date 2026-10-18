<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { sessionGeneration } from '@/services/authState'
import { getApiError } from '@/services/apiError'
import { getTextbookAudioUploadPolicy, uploadTextbookAudioContent, type TextbookAudioTrack } from '@/services/textbookAudioApi'
import { createTextbookUploadAttempt, readTextbookUploadPolicy, readTextbookUploadReceipt, type TextbookUploadAttempt, type TextbookUploadPolicy } from '@/services/textbookAudioUpload'

const props = defineProps<{ bookId: string; unitId: string; refresh: () => Promise<boolean> }>()
// These are actual operation boundaries for the page's later single player.
const emit = defineEmits<{ beforeContentMutation: [trackId: string]; afterRefresh: []; scopeInvalidated: [] }>()
const policy = ref<TextbookUploadPolicy | null>(null)
const canStart = computed(() => policy.value?.enabled === true)
const policyLoading = ref(false)
const policyError = ref('')
const target = shallowRef<TextbookAudioTrack | null>(null)
const file = shallowRef<File | null>(null)
const attempt = shallowRef<TextbookUploadAttempt | null>(null)
const hasAttempt = computed(() => attempt.value !== null)
const sending = ref(false)
const message = ref('')
const input = ref<HTMLInputElement>()
let generation = 0
let policyGeneration = 0
let controller: AbortController | null = null
let policyController: AbortController | null = null
let disposed = false
let confirmation: Promise<boolean> | null = null
let closeConfirmation: (() => void) | null = null
let trigger: HTMLElement | null = null
function clearFile() {
  file.value = null
  if (input.value) input.value.value = ''
}
function invalidate() {
  generation += 1
  controller?.abort()
  controller = null
  sending.value = false
  attempt.value = null
  target.value = null
  clearFile()
  message.value = ''
  closeConfirmation?.()
  closeConfirmation = null
  emit('scopeInvalidated')
}
async function confirmLeave(): Promise<boolean> {
  if (!attempt.value) return true
  if (confirmation) return confirmation
  const own = generation
  const session = sessionGeneration.value
  confirmation = (async () => {
    try {
      const interrupted = new Promise<never>((_, reject) => { closeConfirmation = () => { ElMessageBox.close(); reject(new Error('Scope ended')) } })
      const answer = ElMessageBox.confirm('离开会放弃继续核对原上传结果，不能撤销服务器可能已完成的结果。确定离开？', '放弃继续核对？', {
        confirmButtonText: '放弃核对并离开', cancelButtonText: '继续核对', type: 'warning', distinguishCancelAndClose: true, closeOnHashChange: false
      })
      // Logout closes only this prompt, without waiting for an answer.
      await Promise.race([answer, interrupted])
      return !disposed && own === generation && session === sessionGeneration.value
    } catch { return false }
    finally { confirmation = null; closeConfirmation = null }
  })()
  return confirmation
}
async function open(track: TextbookAudioTrack, event: Event) {
  const button = event.currentTarget as HTMLElement
  if (!await confirmLeave()) return
  invalidate()
  target.value = { ...track }
  trigger = button
  await nextTick()
  input.value?.focus()
}
async function close() {
  if (!await confirmLeave()) return
  invalidate()
  await nextTick()
  if (trigger?.isConnected) trigger.focus()
}
async function loadPolicy() {
  policyController?.abort()
  const own = ++policyGeneration
  const session = sessionGeneration.value
  policyController = new AbortController()
  const signal = policyController.signal
  const current = () => !disposed && own === policyGeneration && session === sessionGeneration.value && !signal.aborted
  policy.value = null
  policyError.value = ''
  if (!props.unitId) { policyLoading.value = false; return }
  policyLoading.value = true
  try {
    const data = await getTextbookAudioUploadPolicy(props.bookId, props.unitId, { signal, lxIsCurrent: current })
    if (current()) policy.value = readTextbookUploadPolicy(data)
  } catch {
    if (current()) policyError.value = '上传限制读取失败；请重试，不会猜测默认限制。'
  } finally { if (current()) policyLoading.value = false }
}
function choose(event: Event) {
  if (attempt.value) return
  const files = Array.from((event.target as HTMLInputElement).files ?? [])
  file.value = null
  message.value = ''
  if (!files.length) return
  if (files.length !== 1) { message.value = '请选择单个 MP3 文件。'; return }
  const chosen = files[0]!
  if (!policy.value?.enabled) { message.value = '上传当前不可用，请先读取上传限制。'; return }
  if (chosen.size < 1 || chosen.size > policy.value.maxUploadBytes) { message.value = '文件大小超出当前上传限制，或文件为空。'; return }
  // Filename and MIME are hints, never evidence that bytes are decodable MP3.
  file.value = chosen
}
function catalogRefreshed() { emit('afterRefresh') }
async function send(replay = false) {
  if (sending.value || !target.value) return
  if (!replay) {
    if (attempt.value || !file.value || !policy.value?.enabled) return
    if (file.value.size > policy.value.maxUploadBytes) { message.value = '文件超出当前上传限制，请重新选择。'; return }
    if (typeof globalThis.crypto?.randomUUID !== 'function') { message.value = '浏览器无法生成安全上传标识。请使用 HTTPS（本机可使用 localhost）后重试，不会发送文件。'; return }
    attempt.value = createTextbookUploadAttempt(props.bookId, props.unitId, target.value.id, target.value.version, file.value)
    if (input.value) input.value.value = ''
  }
  const original = attempt.value
  if (!original) return
  sending.value = true
  message.value = '正在上传；取消等待或离开不能撤销服务器结果。'
  controller = new AbortController()
  const signal = controller.signal
  const own = generation
  const session = sessionGeneration.value
  // The operation owns the result even after its own cancellation, so it can
  // report unknown. Global authentication failures additionally honor signal.
  const current = () => !disposed && own === generation && session === sessionGeneration.value && attempt.value === original
  emit('beforeContentMutation', original.trackId)
  try {
    const data = await uploadTextbookAudioContent(original, { signal, lxIsCurrent: () => current() && !signal.aborted })
    if (!current()) return
    readTextbookUploadReceipt(data, original.trackId)
    // A permanent receipt confirms this operation, never the current row.
    attempt.value = null
    clearFile()
    const stillCurrent = () => !disposed && own === generation && session === sessionGeneration.value
    message.value = '本次上传已确认。新发布先为未可用；请核对当前目录，试听后再明确设为可用。'
    const refreshed = await props.refresh()
    if (!stillCurrent()) return
    if (!refreshed) message.value += ' 当前目录读取失败；请重试读取，不要重复上传。'
    // Another upload must explicitly reopen a current catalog row/version.
    target.value = null
  } catch (error) {
    if (!current()) return
    const status = getApiError(error).status
    if (!signal.aborted && [400, 404, 413, 415, 422].includes(status ?? 0)) {
      message.value = ({ 400: '上传请求无效', 404: '上传目标已不存在', 413: '文件超过服务器大小限制', 415: '文件不是完整可解码的 MP3', 422: '时长超过服务器限制' } as Record<number, string>)[status!]! + '。本次请求被拒绝，请核对目录后重新选择上传。'
      attempt.value = null
      clearFile()
      await props.refresh()
      if (!disposed && own === generation && session === sessionGeneration.value) target.value = null
    } else {
      message.value = status === 409
        ? '可能仍在处理中或条件冲突，结果尚未确认；保留原文件、版本和请求，请手动核对原上传结果。'
        : status === 429
          ? '本次请求未获上传准入；这不能证明此前未知尝试已失败。原尝试保留，请稍后手动核对。'
          : '上传结果未知，服务器可能已完成。原尝试仅保留在内存；请核对原上传结果，不会自动重发或换新请求。取消等待不能撤销服务器结果。'
      await props.refresh()
    }
  } finally {
    if (!disposed && own === generation && session === sessionGeneration.value) sending.value = false
  }
}
async function abandon() {
  if (!await confirmLeave()) return
  invalidate()
  message.value = '已放弃继续核对，不代表撤销服务器结果。先重新读取目录，再选择当前录音开始新的上传。'
  await props.refresh()
}
function beforeUnload(event: BeforeUnloadEvent) {
  if (attempt.value) { event.preventDefault(); event.returnValue = '' }
}
watch(() => [props.bookId, props.unitId], () => { invalidate(); void loadPolicy() }, { immediate: true, flush: 'sync' })
watch(sessionGeneration, () => {
  invalidate()
  policyGeneration += 1
  policyController?.abort()
  policy.value = null
  policyLoading.value = false
  policyError.value = ''
}, { flush: 'sync' })
onMounted(() => window.addEventListener('beforeunload', beforeUnload))
onBeforeUnmount(() => { disposed = true; invalidate(); policyController?.abort(); window.removeEventListener('beforeunload', beforeUnload) })
defineExpose({ open, confirmLeave, invalidate, catalogRefreshed, canStart, hasAttempt, sending, targetId: () => target.value?.id })
</script>

<template>
  <section class="textbook-upload" aria-label="教材录音上传">
    <p v-if="policyLoading" role="status">正在读取上传限制…</p>
    <p v-if="policy" class="upload-policy">音频上传{{ policy.enabled ? '已开启' : '已关闭（仍可维护目录）' }} · 文件上限 {{ policy.maxUploadBytes }} 字节 · 时长上限 {{ policy.maxDurationSeconds }} 秒。</p>
    <p v-if="policyError" role="alert">{{ policyError }}</p>
    <el-button v-if="unitId" :disabled="policyLoading" @click="loadPolicy">重新读取上传限制</el-button>
    <div v-if="target" class="upload-editor">
      <h2>上传录音：{{ target.title }}</h2>
      <p>单个 MP3；文件名和 MIME 只是提示，格式与时长由服务器校验。新发布先为未可用。</p>
      <label for="textbook-audio-file">选择 MP3 文件</label>
      <input id="textbook-audio-file" ref="input" type="file" accept=".mp3,audio/mpeg" :disabled="sending || !!attempt || !policy?.enabled" @change="choose">
      <p v-if="file">已选择：{{ file.name }}（{{ file.size }} 字节）</p>
      <p>原提交条件版本：{{ target.version }}</p>
      <div class="upload-actions">
        <el-button v-if="!attempt" type="primary" :disabled="!file || !policy?.enabled || sending" @click="send()">开始上传</el-button>
        <el-button v-if="attempt && !sending" type="primary" @click="send(true)">核对原上传结果</el-button>
        <el-button v-if="sending" @click="controller?.abort()">取消上传等待（结果可能未知）</el-button>
        <el-button v-if="attempt" @click="abandon">放弃继续核对</el-button>
        <el-button @click="close">关闭上传</el-button>
      </div>
    </div>
    <p class="upload-feedback" role="status" aria-live="polite">{{ message }}</p>
  </section>
</template>

<style scoped>
.textbook-upload { min-width: 0; margin: var(--lx-space-4) 0; overflow-wrap: anywhere; }
.upload-editor { margin-top: var(--lx-space-3); padding: var(--lx-space-4); border: 1px solid var(--lx-color-border-light); border-radius: var(--lx-radius-md); }
.upload-editor h2 { font-size: var(--lx-font-size-md); }
.upload-editor input { display: block; max-width: 100%; margin: var(--lx-space-2) 0; }
.upload-editor input:focus-visible { outline: 2px solid var(--lx-color-focus); outline-offset: 2px; }
.upload-actions { display: flex; flex-wrap: wrap; gap: var(--lx-space-2); }
.upload-actions :deep(.el-button + .el-button) { margin-left: 0; }
.upload-feedback { min-height: 1.5em; }
</style>
