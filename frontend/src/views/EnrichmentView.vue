<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import VocabularyDetailDrawer from '@/components/VocabularyDetailDrawer.vue'
import { sessionGeneration } from '@/services/authState'
import { getApiError } from '@/services/apiError'
import { readEnrichmentDetail, readEnrichmentSummary, readEnrichmentTasks, retryEnrichment, states, steps } from '@/services/enrichmentApi'
import type { EnrichmentDetail, EnrichmentQuery, EnrichmentSummary, EnrichmentTask, RetryAction } from '@/services/enrichmentApi'
import { errorLabel, reasonLabel, stateLabels, stepLabels, taskLabel } from '@/services/enrichmentLabels'

const route = useRoute()
const filters = reactive({ wordId: '', meaningId: '', locale: '', state: '', step: '' })
const query = ref<EnrichmentQuery>({}), page = ref(1), total = ref(0)
const items = ref<EnrichmentTask[]>([]), summary = ref<EnrichmentSummary | null>(null)
const selected = ref(''), historyPage = ref(1), detail = ref<EnrichmentDetail | null>(null)
const wordId = ref<string | null>(null)
const loading = ref(false), mutating = ref(false)
const listError = ref(''), summaryError = ref(''), detailError = ref(''), notice = ref('')
const pending = ref<RetryAction | null>(null), canReplay = ref(false)
const polling = ref('正在读取状态')
const session = sessionGeneration.value
let active = true, generation = 0, writeGeneration = 0, failures = 0
let readController: AbortController | null = null, writeController: AbortController | null = null
let timer: ReturnType<typeof setTimeout> | undefined
const live = () => active && session === sessionGeneration.value
const retryable = computed(() => detail.value && detail.value.task.applicable && detail.value.task.missing
  && detail.value.task.state === 'Failed' && detail.value.task.current.state === 'Failed')
const historyTotal = computed(() => detail.value ? Math.max(...[detail.value.rounds, detail.value.calls, detail.value.waits, detail.value.cleanup].map(p => p.totalCount)) : 0)
function time(value: number | null) { return value === null ? '未安排' : new Date(value).toLocaleString() }
function cancelRead() {
  clearTimeout(timer)
  generation += 1
  readController?.abort()
  loading.value = false
}
function schedule(failed: boolean) {
  if (!live() || document.hidden) { polling.value = '页面隐藏时暂停刷新'; return }
  failures = failed ? failures + 1 : 0
  if (failures >= 3) { polling.value = '连续读取失败，自动刷新已暂停，请手动刷新'; return }
  polling.value = failed ? '读取失败，稍后有限重试' : '每 10 秒自动刷新；新增优先、历史持续推进'
  timer = setTimeout(() => { void refresh() }, 10000 * Math.max(1, failures))
}
async function refresh(manual = false) {
  if (!live() || mutating.value || document.hidden) return
  cancelRead()
  if (manual) failures = 0
  const version = generation, current = () => live() && version === generation
  readController = new AbortController()
  const signal = readController.signal, target = selected.value
  loading.value = true
  const results = await Promise.allSettled([
    readEnrichmentTasks(query.value, page.value, signal, current),
    readEnrichmentSummary(query.value, signal, current),
    target ? readEnrichmentDetail(target, historyPage.value, signal, current) : Promise.resolve(null)
  ])
  if (!current()) return
  const [list, counts, history] = results
  if (list.status === 'fulfilled') { items.value = list.value.items; total.value = list.value.totalCount; listError.value = '' }
  else { items.value = []; total.value = 0; listError.value = '任务列表状态未知：读取失败，请刷新。' }
  if (counts.status === 'fulfilled') { summary.value = counts.value; summaryError.value = '' }
  else { summary.value = null; summaryError.value = '当前目标和服务状态未知：读取失败，请刷新。' }
  if (history.status === 'fulfilled') {
    detail.value = history.value; detailError.value = ''
    if (pending.value && history.value?.task.id === pending.value.id) {
      const task = history.value.task
      if (task.current.number !== pending.value.expectedRound || !task.applicable || !task.missing || task.state !== 'Failed') {
        pending.value = null; canReplay.value = false
        notice.value = '已回读当前状态，原失败轮次已变化；请以服务器当前结果及历史为准。'
      } else {
        canReplay.value = true
        notice.value = '已回读，仍是原失败轮次。本次结果尚未确认，可明确使用原请求重试，或继续刷新。'
      }
    }
  } else {
    detail.value = null; canReplay.value = false
    const missing = getApiError(history.reason).status === 404
    if (missing) pending.value = null
    detailError.value = missing ? '任务不存在或已删除，请选择其他任务。' : '任务详情状态未知：读取失败，请刷新；不会重放请求。'
  }
  loading.value = false
  schedule(results.some(r => r.status === 'rejected'))
}
function search() {
  if (!live() || mutating.value) return
  query.value = Object.fromEntries(Object.entries(filters).filter(([, value]) => value.trim()).map(([key, value]) => [key, value.trim()]))
  page.value = 1
  if (!pending.value) { selected.value = ''; detail.value = null; historyPage.value = 1 }
  items.value = []; summary.value = null
  void refresh(true)
}
function select(task: EnrichmentTask) {
  if (mutating.value || pending.value && pending.value.id !== task.id) return
  selected.value = task.id; historyPage.value = 1; detail.value = null
  void refresh(true)
}
function changePage(delta: number, history = false) {
  if (mutating.value) return
  if (history) { historyPage.value += delta; detail.value = null }
  else { page.value += delta; items.value = [] }
  void refresh(true)
}
async function retry() {
  if (!live() || mutating.value || loading.value || !retryable.value || pending.value && !canReplay.value) return
  const task = detail.value!.task
  const action = pending.value ?? { id: task.id, expectedRound: task.current.number, retryKey: crypto.randomUUID() }
  pending.value = action; canReplay.value = false
  cancelRead(); writeController = new AbortController(); mutating.value = true
  const version = ++writeGeneration, current = () => live() && version === writeGeneration
  try {
    const result = await retryEnrichment(action, writeController.signal, current)
    if (!current()) return
    notice.value = `服务器已接受单条重试（第 ${result.round} 轮${result.replayed ? '，原请求已接受' : ''}）；成功侧不会重做。`
    pending.value = null
  } catch (error: unknown) {
    if (!current()) return
    const status = getApiError(error).status
    notice.value = status === 409 ? '原失败轮次已变化，正在回读；不会自动再次提交。'
      : status === 404 ? '任务已不存在，正在回读。'
        : '重试结果未确认，正在回读；不会自动重放或换用新请求。'
    if (status !== undefined && status < 500) pending.value = null
  } finally {
    if (current()) { mutating.value = false; await refresh(true) }
  }
}
function fromRoute() {
  writeGeneration += 1; writeController?.abort(); mutating.value = false
  pending.value = null; canReplay.value = false; notice.value = ''
  wordId.value = null
  for (const key of ['wordId', 'meaningId'] as const) filters[key] = typeof route.query[key] === 'string' ? route.query[key] : ''
  search()
}
function visibility() {
  if (document.hidden) { cancelRead(); polling.value = '页面隐藏时暂停刷新' }
  else { void refresh(true) }
}
function stop() {
  active = false; writeGeneration += 1; cancelRead(); writeController?.abort()
  pending.value = null; canReplay.value = false; detail.value = null; summary.value = null; items.value = []; wordId.value = null
}
watch(sessionGeneration, stop, { flush: 'sync' })
watch(() => route.fullPath, fromRoute)
onMounted(() => { document.addEventListener('visibilitychange', visibility); fromRoute() })
onBeforeUnmount(() => { stop(); document.removeEventListener('visibilitychange', visibility) })
</script>

<template>
  <section class="enrichment">
    <PageHeader title="自治补齐进度" description="后台自动发现新增与历史缺失，无需选择词书或点击开始。字段、两侧音频、服务与旧文件清理分别记录。" />
    <p>
      <RouterLink to="/settings/text">检查文本补齐</RouterLink> ·
      <RouterLink to="/settings/speech">检查语音生成</RouterLink> ·
      <RouterLink to="/settings/audio-storage">检查音频存储</RouterLink> ·
      <RouterLink to="/settings/background">查看后台策略</RouterLink>
    </p>
    <p>限流和未调用的等待不消耗失败额度；服务恢复不会清零已耗尽任务。修正相关配置或数据后，仍缺失的部分会自动继续。</p>
    <p role="status" aria-label="自动刷新状态">{{ polling }}{{ loading ? ' · 正在读取…' : '' }}</p>
    <p v-if="notice" role="status" aria-label="单条重试结果">{{ notice }}</p>
    <button type="button" :disabled="loading || mutating" @click="refresh(true)">刷新状态</button>
    <form class="filters" aria-label="筛选补齐状态" @submit.prevent="search">
      <label>词条 ID<input v-model="filters.wordId" aria-label="词条 ID" maxlength="128" :disabled="mutating"></label>
      <label>释义 ID<input v-model="filters.meaningId" aria-label="释义 ID" maxlength="128" :disabled="mutating"></label>
      <label>口音<select v-model="filters.locale" aria-label="口音" :disabled="mutating"><option value="">全部口音</option><option value="en-GB">UK</option><option value="en-US">US</option></select></label>
      <label>步骤<select v-model="filters.step" aria-label="步骤" :disabled="mutating"><option value="">全部步骤</option><option v-for="step in steps" :key="step" :value="step">{{ stepLabels[step] }}</option></select></label>
      <label>状态<select v-model="filters.state" aria-label="状态" :disabled="mutating"><option value="">全部状态</option><option v-for="state in states" :key="state" :value="state">{{ stateLabels[state] }}</option></select></label>
      <button :disabled="mutating">应用筛选</button>
    </form>
    <p v-if="summaryError" role="alert">{{ summaryError }}</p>
    <section v-if="summary" aria-label="当前需求与服务">
      <h2>当前需求</h2>
      <p>适用目标 {{ summary.applicableTargets }} · 已完成 {{ summary.completedTargets }} · 仍缺失 {{ summary.missingTargets }} · 尚未排队 {{ summary.unqueuedTargets }}</p>
      <p>持久步骤 {{ summary.persistedTasks }}；包括准备和历史，与适用目标分母不同。列表为空不代表全部完成。</p>
      <ul><li v-for="(count, i) in summary.targets" :key="i">{{ stateLabels[count.state] }} · {{ reasonLabel(count.waitReason) }}：{{ count.count }}</li></ul>
      <h2>服务状态</h2>
      <p v-if="!summary.services.length">暂无服务状态；不能据此确认供应商可用。</p>
      <p v-for="service in summary.services" :key="service.service" :aria-label="`${service.service} 服务状态`">
        {{ service.service === 'text' ? '文本准备' : '语音' }}：{{ service.blocked ? '配置或能力受阻，请检查设置' : service.paused ? '暂停，等待恢复探测' : '未暂停' }} · 连续服务失败 {{ service.consecutiveFailures }} · 共享限流退避 {{ service.throttleSeconds }} 秒 · 下次处理 {{ time(service.nextAttemptAtUnixMs) }}
      </p>
    </section>
    <h2>持久任务步骤</h2>
    <p v-if="listError" role="alert">{{ listError }}</p>
    <p v-else-if="!items.length && !loading">没有匹配的持久任务；请同时查看当前需求、关闭或未排队状态。</p>
    <div class="table-scroll">
      <table v-if="items.length" aria-label="补齐任务列表">
        <thead><tr><th>目标 / 口音 / 步骤</th><th>当前状态</th><th>实际请求 / 失败额度</th><th>下次处理</th><th>旧文件清理</th><th>操作</th></tr></thead>
        <tbody>
          <tr v-for="task in items" :key="task.id">
            <td>{{ task.word ?? task.wordId }}<br>{{ task.meaning ?? '默认目标' }} · {{ task.locale === 'en-GB' ? 'UK' : 'US' }} · {{ stepLabels[task.step] }}</td>
            <td>{{ taskLabel(task) }}<br>{{ reasonLabel(task.waitReason) }}<br>{{ errorLabel(task.current.errorCode) }}</td>
            <td>请求 {{ task.current.requestCount }}<br>已消耗 {{ task.current.consumedFailures }} / {{ task.current.failureBudget }}（第 {{ task.current.number }} 轮）</td>
            <td>{{ time(task.current.nextAttemptAtUnixMs) }}</td>
            <td>等待 {{ task.cleanup.waiting }} · 处理中 {{ task.cleanup.running }} · 已观察清理 {{ task.cleanup.observed }}<br>{{ errorLabel(task.cleanup.errorCode) }}</td>
            <td><button type="button" :disabled="mutating || !!pending && pending.id !== task.id" :aria-label="`查看 ${task.word ?? task.wordId} ${task.locale} ${stepLabels[task.step]} 详情`" @click="select(task)">详情</button></td>
          </tr>
        </tbody>
      </table>
    </div>
    <nav aria-label="任务分页"><button :disabled="page <= 1 || mutating" @click="changePage(-1)">上一页任务</button> 第 {{ page }} 页 · {{ total }} 条 <button :disabled="page * 25 >= total || mutating" @click="changePage(1)">下一页任务</button></nav>
    <section v-if="selected" aria-label="任务详情">
      <h2>任务详情与历史</h2>
      <p v-if="detailError" role="alert">{{ detailError }}</p>
      <template v-if="detail">
        <p>{{ detail.task.word ?? detail.task.wordId }} · {{ detail.task.meaning ?? '默认目标' }} · {{ detail.task.locale }} · {{ stepLabels[detail.task.step] }}</p>
        <p>{{ taskLabel(detail.task) }} · {{ reasonLabel(detail.task.waitReason) }} · {{ errorLabel(detail.task.current.errorCode) }}</p>
        <p>当前第 {{ detail.task.current.number }} 轮；持久状态 {{ stateLabels[detail.task.current.state] }}。实际请求 {{ detail.task.current.requestCount }}；已消耗失败额度 {{ detail.task.current.consumedFailures }} / {{ detail.task.current.failureBudget }}；下次处理 {{ time(detail.task.current.nextAttemptAtUnixMs) }}。</p>
        <p v-if="detail.task.cleanup.waiting || detail.task.cleanup.running || detail.task.cleanup.observed">旧专用音频已停用；旧文件等待清理 {{ detail.task.cleanup.waiting }}，清理中 {{ detail.task.cleanup.running }}，已观察清理 {{ detail.task.cleanup.observed }}。清理尝试 {{ detail.task.cleanup.attempts }}，下次 {{ time(detail.task.cleanup.nextAttemptAtUnixMs) }}。{{ errorLabel(detail.task.cleanup.errorCode) }}。清理与新生成状态独立，不恢复旧绑定，也不消耗生成失败额度。</p>
        <button type="button" :disabled="mutating" @click="wordId = detail.task.wordId">查看词条并编辑数据</button>
        <button v-if="retryable" type="button" :disabled="loading || mutating || !!pending && !canReplay" @click="retry">{{ pending ? '使用原请求重试此失败步骤' : '仅重试此失败步骤' }}</button>
        <p v-if="pending">正在核对单条重试结果；不会自动重复提交。先读取当前状态，再决定是否用原请求重试。</p>
        <h3>轮次历史（{{ detail.rounds.totalCount }}）</h3>
        <ul><li v-for="round in detail.rounds.items" :key="round.number">第 {{ round.number }} 轮 · {{ stateLabels[round.state] }} · 请求 {{ round.requestCount }} · 失败额度 {{ round.consumedFailures }} / {{ round.failureBudget }} · {{ reasonLabel(round.waitReason) }} · {{ errorLabel(round.errorCode) }} · 建立 {{ time(round.createdAtUnixMs) }} · 更新 {{ time(round.updatedAtUnixMs) }} · 下次 {{ time(round.nextAttemptAtUnixMs) }}</li></ul>
        <h3>实际请求历史（{{ detail.calls.totalCount }}）</h3>
        <ul><li v-for="call in detail.calls.items" :key="call.id">第 {{ call.round }} 轮 · {{ call.service }} · {{ call.result }} · {{ errorLabel(call.errorCode) }} · {{ time(call.startedAtUnixMs) }} → {{ time(call.finishedAtUnixMs) }}</li></ul>
        <h3>等待历史（{{ detail.waits.totalCount }}）</h3>
        <ul><li v-for="(wait, i) in detail.waits.items" :key="i">第 {{ wait.round }} 轮 · {{ reasonLabel(wait.reason) }} · {{ errorLabel(wait.errorCode) }} · {{ time(wait.recordedAtUnixMs) }} · 下次 {{ time(wait.nextAttemptAtUnixMs) }}</li></ul>
        <h3>独立清理历史（{{ detail.cleanup.totalCount }}）</h3>
        <ul><li v-for="(cleanup, i) in detail.cleanup.items" :key="i">失效代次 {{ cleanup.epoch }} · {{ cleanup.state }} · 尝试 {{ cleanup.attempts }} · {{ errorLabel(cleanup.errorCode) }} · 建立 {{ time(cleanup.createdAtUnixMs) }} · 更新 {{ time(cleanup.updatedAtUnixMs) }} · 下次 {{ time(cleanup.nextAttemptAtUnixMs) }}</li></ul>
      </template>
      <nav aria-label="历史分页"><button :disabled="historyPage <= 1 || mutating" @click="changePage(-1, true)">上一页历史</button> 第 {{ historyPage }} 页 <button :disabled="!detail || historyPage * 10 >= historyTotal || mutating" @click="changePage(1, true)">下一页历史</button></nav>
    </section>
    <VocabularyDetailDrawer v-model="wordId" @changed="refresh(true)" />
  </section>
</template>

<style scoped>
.filters { display: flex; flex-wrap: wrap; gap: var(--lx-space-3); margin: var(--lx-space-4) 0; align-items: end; }
label { display: flex; flex-direction: column; gap: var(--lx-space-1); }
button, input, select { min-height: 36px; padding: var(--lx-space-2); border: 1px solid var(--lx-color-border); border-radius: var(--lx-radius-sm); color: var(--lx-color-text-primary); background: var(--lx-color-bg-surface); }
button { cursor: pointer; }
button:disabled { cursor: default; opacity: .55; }
button:focus-visible, input:focus-visible, select:focus-visible { outline: 2px solid var(--lx-color-focus); outline-offset: 2px; }
p, li, td { overflow-wrap: anywhere; line-height: 1.6; }
.table-scroll { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; }
th, td { text-align: left; vertical-align: top; padding: var(--lx-space-3); border-bottom: 1px solid var(--lx-color-border-light); min-width: 110px; }
nav { margin: var(--lx-space-3) 0; }
section[aria-label='任务详情'] { border-top: 1px solid var(--lx-color-border); margin-top: var(--lx-space-4); }
</style>
