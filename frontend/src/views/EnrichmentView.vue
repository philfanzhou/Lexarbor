<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import VocabularyDetailDrawer from '@/components/VocabularyDetailDrawer.vue'
import { sessionGeneration } from '@/services/authState'
import { getApiError } from '@/services/apiError'
import { readEnrichmentDetail, readEnrichmentSummary, readEnrichmentTasks, retryEnrichment, states, steps } from '@/services/enrichmentApi'
import type { EnrichmentDetail, EnrichmentQuery, EnrichmentSummary, EnrichmentTask, RetryAction } from '@/services/enrichmentApi'
import { errorLabel, reasonLabel, stateLabels, stepLabels, taskLabel, taskStateLabel, activityLabels, activityDescriptions, requirementLabels, requirementLinks, guidance, callLabels, cleanupLabels } from '@/services/enrichmentLabels'
import { newRetryKey } from '@/services/retryKey'

const route = useRoute()
const filters = reactive({ search: '', wordId: '', meaningId: '', locale: '', state: '', step: '', sort: 'attention' })
const attentionOnly = ref(false), diagnostics = ref(false), advanced = ref(false)
const detailHeading = ref<HTMLElement | null>(null)
const taskButtons = ref<HTMLButtonElement[]>([])
const lastUpdated = ref<number | null>(null)
const taskGuidance = computed(() => detail.value ? guidance(detail.value.task) : null)
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
  && !detail.value.task.automaticRecoveryAvailable
  && detail.value.task.state === 'Failed' && detail.value.task.current.state === 'Failed')
const historyTotal = computed(() => detail.value ? Math.max(...[detail.value.rounds, detail.value.calls, detail.value.waits, detail.value.cleanup].map(p => p.totalCount)) : 0)
function time(value: number | null) { return value === null ? '未安排' : new Date(value).toLocaleString() }
function nextTime(task: EnrichmentTask) { if (task.automaticRecoveryAvailable) return '后台即将自动恢复'; if (task.current.automaticRecovery && task.state === 'Failed') return '自动恢复已停止，待复核'; return !task.missing || !task.applicable ? '无需处理' : task.state === 'Failed' ? '处理原因后可重试' : task.waitReason === 'PreparationPending' ? '前置发音准备完成后自动继续' : task.waitReason === 'ConfigurationMissing' || task.waitReason === 'StorageDisabled' ? '配置就绪后自动继续' : task.current.nextAttemptAtUnixMs ? time(task.current.nextAttemptAtUnixMs) : task.state === 'Running' ? '正在处理' : '后台自动安排' }
function focusStep(step: string) { filters.step = filters.step === step ? '' : step; search() }
function quick(value: string) { attentionOnly.value = value === 'attention'; filters.state = value === 'attention' ? '' : value; search() }
function resetFilters() { Object.assign(filters, { search: '', wordId: '', meaningId: '', locale: '', state: '', step: '', sort: 'attention' }); attentionOnly.value = false; search() }
async function closeDetail() {
  if (mutating.value || pending.value) return
  const previous = selected.value
  selected.value = ''; detail.value = null; diagnostics.value = false
  await refresh(true); await nextTick()
  if (live() && !selected.value) taskButtons.value.find(button => button.dataset.taskId === previous)?.focus()
}
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
    readEnrichmentSummary({ wordId: query.value.wordId, meaningId: query.value.meaningId, search: query.value.search, locale: query.value.locale }, signal, current),
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
  if (list.status === 'fulfilled' && counts.status === 'fulfilled') lastUpdated.value = Date.now()
  loading.value = false
  schedule(results.some(r => r.status === 'rejected'))
}
function search() {
  if (!live() || mutating.value) return
  query.value = { ...Object.fromEntries(Object.entries(filters).filter(([, value]) => value.trim()).map(([key, value]) => [key, value.trim()])), attentionOnly: attentionOnly.value || undefined } as EnrichmentQuery
  page.value = 1
  if (!pending.value) { selected.value = ''; detail.value = null; historyPage.value = 1 }
  items.value = []; summary.value = null
  void refresh(true)
}
async function select(task: EnrichmentTask) {
  if (mutating.value || pending.value && pending.value.id !== task.id) return
  selected.value = task.id; historyPage.value = 1; detail.value = null; diagnostics.value = false
  await refresh(true)
  if (live() && selected.value === task.id) { await nextTick(); detailHeading.value?.focus({ preventScroll: true }); detailHeading.value?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }) }
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
  const action = pending.value ?? { id: task.id, expectedRound: task.current.number, retryKey: newRetryKey() }
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
  advanced.value = !!filters.wordId || !!filters.meaningId
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
    <PageHeader title="词汇自动完善" description="查看音标、词性、扩词和读音的处理进度。配置就绪后后台自动运行，离开页面不影响任务。">
      <template #actions><button type="button" :disabled="loading || mutating" @click="refresh(true)">刷新状态</button><RouterLink to="/submissions">投稿自动审核 →</RouterLink></template>
    </PageHeader>
    <div class="refresh-line"><span role="status" aria-label="自动刷新状态">{{ polling }}{{ loading ? ' · 正在读取…' : '' }}</span><span v-if="lastUpdated">上次更新 {{ new Date(lastUpdated).toLocaleTimeString() }}</span></div>
    <p v-if="notice" class="notice" role="status" aria-label="单条重试结果">{{ notice }}</p>
    <p v-if="summaryError" class="notice error" role="alert">{{ summaryError }}</p>
    <section v-if="summary" aria-label="当前需求与服务" class="overview">
      <div class="section-heading"><h2>当前词汇范围</h2><span class="muted">按单词搜索、口音及精确定位统计，不随任务状态筛选变化</span></div>
      <p v-if="summary.discovery" class="muted" aria-label="历史扫描进度">{{ summary.discovery.initialCompleted ? '首次历史扫描已完成，后台持续检查新增和变更。' : !summary.discovery.wordsTotal && !summary.discovery.meaningsTotal ? '历史扫描尚未开始，文本配置就绪后自动进行。' : '首次接管历史数据，正在分批发现任务。' }} 已扫描单词 {{ summary.discovery.wordsScanned }} / {{ summary.discovery.wordsTotal }}，释义 {{ summary.discovery.meaningsScanned }} / {{ summary.discovery.meaningsTotal }}。扫描完成不代表 AI 已处理完毕。</p>
      <p v-if="summary.destination" class="muted" aria-label="扩词目标词书">扩词目标词书：{{ summary.destination.state === 'Disabled' ? '已停用，扩词暂停；启用后自动继续。' : summary.destination.state === 'Missing' ? '尚未就绪，文本配置就绪后后台自动准备。' : summary.destination.managed ? '程序管理的补充词书已就绪。' : '指定词书已就绪。' }}<RouterLink to="/books">查看词书</RouterLink></p>
      <p v-if="summary.execution" class="muted" aria-label="语音请求统计">语音模型实际请求 {{ summary.execution.speechRequests }} 次，成功 {{ summary.execution.speechSucceeded }} 次；最近一次模型请求成功：{{ time(summary.execution.lastSucceededAtUnixMs) }}。已有音频直接沿用，无需重新生成。</p>
      <div class="activities">
        <article v-for="activity in summary.activities" :key="activity.step" class="activity" :class="{ chosen: filters.step === activity.step }" :aria-label="activityLabels[activity.step]">
          <div class="section-heading"><h3>{{ activityLabels[activity.step] }}</h3><span class="badge" :class="activity.actionRequired || activity.missingRequirements.length ? 'attention' : activity.total && activity.completed === activity.total ? 'success' : 'neutral'">{{ activity.missingRequirements.length ? '配置未就绪' : activity.actionRequired ? '需要处理' : !activity.total ? '暂无目标' : activity.completed === activity.total ? '已完成' : '后台自动处理' }}</span></div>
          <p class="muted">{{ activityDescriptions[activity.step] }}</p>
          <p class="progress-count"><strong>{{ activity.completed }}</strong><span> / {{ activity.total }} 个目标已完成</span></p>
          <progress v-if="activity.total" :value="activity.completed" :max="activity.total" :aria-label="`${activityLabels[activity.step]}完成进度`" />
          <p v-if="activity.total" class="muted">待完善 {{ activity.total - activity.completed }}<span v-if="activity.actionRequired"> · 需要处理 {{ activity.actionRequired }}</span><span v-if="activity.unqueued"> · 待扫描排队 {{ activity.unqueued }}</span></p>
          <p v-else class="muted">当前范围没有适用词汇。</p>
          <ul v-if="activity.missingRequirements.length" class="requirements"><li v-for="requirement in activity.missingRequirements" :key="requirement"><RouterLink :to="requirementLinks[requirement] ?? '/settings/text'">{{ requirementLabels[requirement] }}</RouterLink><span v-if="requirement === 'supplement'">：后台会自动准备缺失的补充词书；已停用的词书需重新启用</span></li></ul>
          <button class="text-button" :aria-pressed="filters.step === activity.step" :disabled="mutating || !!pending" @click="focusStep(activity.step)">{{ filters.step === activity.step ? '显示全部任务' : '查看相关任务' }}</button>
        </article>
      </div>
      <p v-if="summary.textRequests" class="muted" aria-label="文本请求统计">批量调度启用以来，全站文本请求 {{ summary.textRequests.requests }} 次，处理 {{ summary.textRequests.participations }} 项任务，已保存 {{ summary.textRequests.completed }} 项有效结果<span v-if="summary.textRequests.unknown">；{{ summary.textRequests.unknown }} 次请求正在处理或结果尚未确认</span>。包含补齐、词变和自动审核；一次请求可处理多项任务。</p>
      <div class="overview-footer"><p>已完成 {{ summary.completedTargets }} / {{ summary.applicableTargets }} 个目标 · 仍需完善 {{ summary.missingTargets }}<span v-if="summary.unqueuedTargets"> · {{ summary.unqueuedTargets }} 个等待后台扫描排队</span></p><span class="muted">同一词可能有多个口音或释义目标，任务数不等于单词数。</span></div>
      <div class="service-strip">
        <p v-for="service in summary.services" :key="service.service" :aria-label="`${service.service} 服务状态`"><span class="status-dot" :class="{ blocked: service.blocked || service.paused }" />{{ service.service === 'text' ? '文本模型' : '语音模型' }}：{{ service.blocked ? '配置或凭据受阻' : service.paused ? '暂时不可用，后台等待恢复' : service.nextAttemptAtUnixMs && service.nextAttemptAtUnixMs > Date.now() ? '限流等待，稍后自动继续' : '未暂停' }}<RouterLink v-if="service.blocked" :to="service.service === 'text' ? '/settings/text' : '/settings/speech'">检查配置</RouterLink></p>
        <RouterLink to="/settings/background">后台策略</RouterLink>
      </div>
    </section>

    <section class="task-section" aria-label="任务管理">
      <div class="section-heading"><h2>任务列表</h2><span class="muted">优先处理异常，正常等待由系统自动恢复</span></div>
      <form class="filters" aria-label="筛选补齐状态" @submit.prevent="search">
        <label class="word-search">搜索单词或释义<input v-model="filters.search" type="search" aria-label="搜索单词或释义" placeholder="例如 lead 或 引导" maxlength="200" :disabled="mutating"></label>
        <label>步骤<select v-model="filters.step" aria-label="步骤" :disabled="mutating"><option value="">全部任务类型</option><option v-for="step in steps" :key="step" :value="step">{{ activityLabels[step] ?? stepLabels[step] }}</option></select></label>
        <label>口音<select v-model="filters.locale" aria-label="口音" :disabled="mutating"><option value="">全部口音（含扩词）</option><option value="en-GB">英式 UK</option><option value="en-US">美式 US</option></select></label>
        <button class="primary" :disabled="mutating">应用筛选</button><button type="button" :disabled="mutating" @click="resetFilters">重置筛选</button>
      </form>
      <div class="task-toolbar">
        <div class="quick-filters" aria-label="常用任务筛选"><button v-for="option in [{ value: '', text: '全部' }, { value: 'attention', text: '需要处理' }, { value: 'Running', text: '处理中' }, { value: 'Waiting', text: '等待中' }, { value: 'Succeeded', text: '已完成' }]" :key="option.value" :aria-pressed="option.value === 'attention' ? attentionOnly : !attentionOnly && filters.state === option.value" :disabled="mutating" @click="quick(option.value)">{{ option.text }}</button></div>
        <label class="sort">排序<select v-model="filters.sort" aria-label="任务排序" :disabled="mutating" @change="search"><option value="attention">需要处理优先</option><option value="recent">最近更新优先</option><option value="word">词条顺序</option></select></label>
      </div>
      <details :open="advanced" class="advanced" @toggle="advanced = ($event.target as HTMLDetailsElement).open">
        <summary>精确定位与更多状态</summary><div class="filters">
          <label>词条 ID<input v-model="filters.wordId" aria-label="词条 ID" maxlength="128" :disabled="mutating"></label><label>释义 ID<input v-model="filters.meaningId" aria-label="释义 ID" maxlength="128" :disabled="mutating"></label>
          <label>状态<select v-model="filters.state" aria-label="状态" :disabled="mutating" @change="attentionOnly = false"><option value="">全部状态</option><option v-for="state in states" :key="state" :value="state">{{ stateLabels[state] }}</option></select></label><button :disabled="mutating" @click="search">应用精确筛选</button>
        </div>
      </details>
      <p v-if="filters.locale" class="muted">当前仅显示{{ filters.locale === 'en-GB' ? '英式' : '美式' }}目标。词汇扩展不区分口音，选择“全部口音”可查看。</p>
      <p v-if="attentionOnly" class="muted">显示本轮失败、缺少配置或存储的任务；服务限流和自动重试无需人工处理。</p>
      <p v-if="listError" class="notice error" role="alert">{{ listError }}</p>
      <div v-else-if="!items.length && !loading" class="empty-state"><h3>{{ attentionOnly ? '当前没有需要处理的已排队任务' : '没有符合筛选条件的任务' }}</h3><p>{{ summary?.unqueuedTargets ? `仍有 ${summary.unqueuedTargets} 个目标等待扫描排队，请查看上方配置提示。` : '可以调整筛选范围，或查看上方各类任务的进度。' }}</p><button :disabled="mutating" @click="resetFilters">清除筛选</button></div>
      <div class="workspace" :class="{ 'with-detail': selected }">
        <div class="task-list">
          <div class="table-scroll">
            <table v-if="items.length" aria-label="补齐任务列表">
              <thead><tr><th>单词与任务</th><th>状态与下一步</th><th>下次处理</th><th>操作</th></tr></thead><tbody>
                <tr v-for="task in items" :key="task.id" :class="{ selected: selected === task.id }">
                  <td><strong class="word">{{ task.word ?? '词条已删除' }}</strong><p v-if="task.meaning" class="meaning">{{ task.meaning }}</p><span class="muted">{{ activityLabels[task.step] ?? stepLabels[task.step] }} · {{ task.locale === null ? '不限口音' : task.locale === 'en-GB' ? '英式 UK' : '美式 US' }}{{ task.step === 'Audio' && task.meaningId ? ' · 释义专用' : '' }}</span></td>
                  <td><span class="badge" :class="task.state === 'Failed' && !task.automaticRecoveryAvailable ? 'attention' : task.state === 'Succeeded' ? 'success' : 'neutral'">{{ taskStateLabel(task) }}</span><p>{{ !task.applicable || !task.missing ? taskLabel(task) : task.automaticRecoveryAvailable || task.current.automaticRecovery ? taskLabel(task) : task.current.errorCode && task.state === 'Failed' ? errorLabel(task.current.errorCode) : task.waitReason ? reasonLabel(task.waitReason) : taskLabel(task) }}</p><p v-if="task.cleanup.waiting || task.cleanup.running" class="muted">旧音频在后台清理，不影响本次生成结果。</p></td>
                  <td class="muted">{{ nextTime(task) }}</td>
                  <td><button ref="taskButtons" :data-task-id="task.id" type="button" class="text-button" :disabled="mutating || !!pending && pending.id !== task.id" :aria-label="`查看 ${task.word ?? task.wordId} ${task.locale ?? '不限口音'} ${stepLabels[task.step]} 详情`" @click="select(task)">{{ task.state === 'Failed' ? '查看并处理' : '查看详情' }}</button></td>
                </tr>
              </tbody>
            </table>
          </div>
          <nav aria-label="任务分页"><span>共 {{ total }} 条 · 第 {{ page }} / {{ Math.max(1, Math.ceil(total / 25)) }} 页</span><div><button :disabled="page <= 1 || loading || mutating" @click="changePage(-1)">上一页任务</button><button :disabled="page * 25 >= total || loading || mutating" @click="changePage(1)">下一页任务</button></div></nav>
        </div>
        <section v-if="selected" aria-label="任务详情" class="task-detail">
          <div class="section-heading"><h2 ref="detailHeading" tabindex="-1">任务详情</h2><button :disabled="mutating || !!pending" aria-label="关闭任务详情" @click="closeDetail">关闭</button></div>
          <p v-if="detailError" class="notice error" role="alert">{{ detailError }}</p><p v-else-if="!detail" role="status">正在读取详情…</p>
          <template v-if="detail">
            <h3 class="detail-word">{{ detail.task.word ?? '词条已删除' }}</h3><p>{{ detail.task.meaning ?? '此单词的默认资料' }}</p><p class="muted">{{ stepLabels[detail.task.step] }} · {{ detail.task.locale ?? '不限口音' }}</p>
            <div v-if="taskGuidance" class="guidance" :class="{ 'needs-action': detail.task.state === 'Failed' && !detail.task.automaticRecoveryAvailable }"><h3>{{ taskGuidance.title }}</h3><p>{{ taskGuidance.description }}</p><RouterLink v-if="taskGuidance.link" :to="taskGuidance.link">{{ taskGuidance.action }} →</RouterLink></div>
            <p>{{ taskLabel(detail.task) }}<span v-if="detail.task.waitReason"> · {{ reasonLabel(detail.task.waitReason) }}</span></p>
            <div class="detail-actions"><button type="button" :disabled="mutating" @click="wordId = detail.task.wordId">查看词条并编辑数据</button><button v-if="retryable" class="primary" type="button" :disabled="loading || mutating || !!pending && !canReplay" @click="retry">{{ pending ? '使用原请求重试此失败步骤' : '仅重试此失败步骤' }}</button></div>
            <p v-if="pending" class="notice">正在核对单条重试结果；不会自动重复提交。先读取当前状态，再决定是否用原请求重试。</p>
            <dl class="task-facts"><div><dt>下次处理</dt><dd>{{ nextTime(detail.task) }}</dd></div><div><dt>最近更新</dt><dd>{{ time(detail.task.current.updatedAtUnixMs) }}</dd></div><div><dt>本轮模型请求</dt><dd>{{ detail.task.current.requestCount }} 次</dd></div><div><dt>失败额度</dt><dd>已消耗 {{ detail.task.current.consumedFailures }} / {{ detail.task.current.failureBudget }}<span class="muted">（限流不扣额度）</span></dd></div></dl>
            <div v-if="detail.task.cleanup.waiting || detail.task.cleanup.running || detail.task.cleanup.observed" class="cleanup-note"><h3>旧音频清理</h3><p>旧专用音频已停用；等待 {{ detail.task.cleanup.waiting }} · 清理中 {{ detail.task.cleanup.running }} · 已清理 {{ detail.task.cleanup.observed }}。清理尝试 {{ detail.task.cleanup.attempts }}。</p><p v-if="detail.task.cleanup.errorCode">{{ errorLabel(detail.task.cleanup.errorCode) }}</p><p class="muted">清理与新生成分别进行，不恢复旧绑定，也不消耗生成失败额度。</p></div>
            <details :open="diagnostics" class="diagnostics" @toggle="diagnostics = ($event.target as HTMLDetailsElement).open">
              <summary>技术详情与执行历史</summary>
              <p>当前第 {{ detail.task.current.number }} 轮 · 持久状态 {{ stateLabels[detail.task.current.state] }}；下次处理 {{ time(detail.task.current.nextAttemptAtUnixMs) }}。</p>
              <p class="muted">词条 ID：{{ detail.task.wordId }}<br>任务 ID：{{ detail.task.id }}</p>
              <h3>轮次历史（{{ detail.rounds.totalCount }}）</h3><ul><li v-for="round in detail.rounds.items" :key="round.number">第 {{ round.number }} 轮 · {{ stateLabels[round.state] }} · 请求 {{ round.requestCount }} · 失败额度 {{ round.consumedFailures }} / {{ round.failureBudget }}<p v-if="round.errorCode">{{ errorLabel(round.errorCode) }}</p><span class="muted">更新 {{ time(round.updatedAtUnixMs) }}</span></li></ul>
              <h3>模型请求（{{ detail.calls.totalCount }}）</h3><ul><li v-for="call in detail.calls.items" :key="call.id">第 {{ call.round }} 轮 · {{ call.service === 'text' ? '文本模型' : call.service === 'tts' ? '语音模型' : '未知服务' }} · {{ callLabels[call.result] ?? '状态未知' }}<p v-if="call.errorCode">{{ errorLabel(call.errorCode) }}</p><span class="muted">{{ time(call.startedAtUnixMs) }} → {{ call.finishedAtUnixMs ? time(call.finishedAtUnixMs) : '结果待确认' }}</span></li></ul>
              <h3>等待记录（{{ detail.waits.totalCount }}）</h3><ul><li v-for="(wait, i) in detail.waits.items" :key="i">第 {{ wait.round }} 轮 · {{ reasonLabel(wait.reason) }}<p class="muted">{{ time(wait.recordedAtUnixMs) }} · 下次 {{ time(wait.nextAttemptAtUnixMs) }}</p></li></ul>
              <h3>清理记录（{{ detail.cleanup.totalCount }}）</h3><ul><li v-for="(cleanup, i) in detail.cleanup.items" :key="i">{{ cleanupLabels[cleanup.state] ?? '状态未知' }} · 尝试 {{ cleanup.attempts }}<p v-if="cleanup.errorCode">{{ errorLabel(cleanup.errorCode) }}</p><span class="muted">更新 {{ time(cleanup.updatedAtUnixMs) }}</span></li></ul>
              <nav aria-label="历史分页"><button :disabled="historyPage <= 1 || loading || mutating" @click="changePage(-1, true)">上一页历史</button><span>第 {{ historyPage }} 页</span><button :disabled="historyPage * 10 >= historyTotal || loading || mutating" @click="changePage(1, true)">下一页历史</button></nav>
            </details>
          </template>
        </section>
      </div>
    </section>
    <VocabularyDetailDrawer v-model="wordId" @changed="refresh(true)" />
  </section>
</template>

<style scoped>
.enrichment { max-width: 1600px; margin: 0 auto; }
a { color: var(--lx-color-primary); }
h2, h3, p { margin: 0; } h2 { font-size: 18px; } h3 { font-size: 15px; }
p, li, td, dd { overflow-wrap: anywhere; line-height: 1.6; }
.muted, .refresh-line { color: var(--lx-color-text-secondary); font-size: 13px; }
.refresh-line, .section-heading, .task-toolbar, .overview-footer, nav { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
.refresh-line { margin-bottom: 20px; }.section-heading { margin-bottom: 12px; }
.activities { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px; }
.activity, .task-section { padding: 20px; border: 1px solid var(--lx-color-border); border-radius: 12px; background: var(--lx-color-bg-surface); }
.activity { display: flex; flex-direction: column; gap: 10px; }.activity .section-heading { margin: 0; }.activity.chosen { border-color: var(--lx-color-primary); box-shadow: 0 0 0 1px var(--lx-color-primary); }
.activity .text-button { align-self: start; margin-top: auto; }.progress-count { display: flex; align-items: baseline; gap: 4px; }.progress-count strong { font-size: 30px; font-variant-numeric: tabular-nums; }.progress-count span { color: var(--lx-color-text-secondary); font-size: 13px; }
progress { width: 100%; height: 7px; border: 0; border-radius: 8px; overflow: hidden; accent-color: var(--lx-color-primary); } progress::-webkit-progress-bar { background: var(--lx-color-bg-page); } progress::-webkit-progress-value { background: var(--lx-color-primary); }
.requirements { margin: 0; padding-left: 18px; font-size: 13px; }.overview-footer { padding: 14px 0 8px; font-size: 14px; }.service-strip { display: flex; align-items: center; flex-wrap: wrap; gap: 12px 24px; margin-bottom: 24px; font-size: 13px; }.service-strip p { display: flex; align-items: center; gap: 8px; }
.status-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--lx-color-text-secondary); }.status-dot.blocked { background: var(--lx-color-warning); }
.badge { display: inline-flex; border-radius: 5px; padding: 3px 8px; font-size: 12px; font-weight: 600; white-space: nowrap; }.neutral { background: var(--lx-color-bg-page); color: var(--lx-color-text-primary); }.attention { background: #fff0da; color: #854600; }.success { background: #eaf5ed; color: #23603a; }
.filters { display: flex; flex-wrap: wrap; gap: 12px; align-items: end; margin: 16px 0; }label { display: flex; flex-direction: column; gap: 6px; font-size: 13px; }.word-search { flex: 1; min-width: 200px; }.sort { flex-direction: row; align-items: center; }
button, input, select { min-height: 40px; padding: 8px 12px; border: 1px solid var(--lx-color-border); border-radius: 6px; color: var(--lx-color-text-primary); background: var(--lx-color-bg-surface); font: inherit; }button { cursor: pointer; }button:disabled { cursor: default; opacity: .55; }.primary { color: white; background: var(--lx-color-primary); border-color: var(--lx-color-primary); }.text-button { border-color: transparent; color: var(--lx-color-primary); padding-inline: 0; }
button:focus-visible, input:focus-visible, select:focus-visible, summary:focus-visible, h2:focus-visible { outline: 2px solid var(--lx-color-focus); outline-offset: 3px; }
.quick-filters { display: flex; gap: 6px; flex-wrap: wrap; }.quick-filters button { font-size: 13px; border-color: transparent; }.quick-filters button[aria-pressed='true'] { color: var(--lx-color-primary); background: var(--lx-color-primary-soft); border-color: var(--lx-color-primary); }
summary { cursor: pointer; padding: 12px 0; font-size: 13px; color: var(--lx-color-text-secondary); }.advanced { border-bottom: 1px solid var(--lx-color-border-light); margin-bottom: 8px; }
.workspace { display: grid; grid-template-columns: minmax(0, 1fr); gap: 20px; }.workspace.with-detail { grid-template-columns: minmax(0, 1fr) minmax(330px, 38%); }.task-list { min-width: 0; }.table-scroll { overflow-x: auto; }table { width: 100%; border-collapse: collapse; font-size: 13px; }th, td { text-align: left; vertical-align: top; padding: 16px 12px; border-bottom: 1px solid var(--lx-color-border-light); }th { color: var(--lx-color-text-secondary); font-size: 12px; font-weight: 500; }td p { margin-top: 5px; }.word { display: block; font-size: 16px; }.meaning { max-width: 28ch; }.selected { background: var(--lx-color-primary-soft); }nav { margin-top: 16px; font-size: 13px; }nav div { display: flex; gap: 8px; }
.task-detail { background: var(--lx-color-bg-page); border: 1px solid var(--lx-color-border); padding: 20px; border-radius: 10px; min-width: 0; align-self: start; }.task-detail > p { margin: 8px 0; }.detail-word { font-size: 24px; }.guidance { margin: 16px 0; padding: 16px; background: var(--lx-color-bg-surface); border-left: 3px solid var(--lx-color-primary); border-radius: 4px; }.guidance p { margin: 6px 0; font-size: 14px; }.guidance.needs-action { border-color: var(--lx-color-warning); }.detail-actions { display: flex; flex-wrap: wrap; gap: 8px; margin: 16px 0; }
.task-facts { margin: 16px 0; font-size: 13px; }.task-facts div { display: flex; gap: 12px; margin: 8px 0; }.task-facts dt { min-width: 90px; color: var(--lx-color-text-secondary); }.task-facts dd { margin: 0; }.cleanup-note { padding: 12px 0; font-size: 13px; border-top: 1px solid var(--lx-color-border); }.cleanup-note p { margin-top: 6px; }.diagnostics { border-top: 1px solid var(--lx-color-border); font-size: 13px; }.diagnostics h3 { margin-top: 20px; }.diagnostics ul { padding-left: 20px; }.diagnostics li { margin: 12px 0; }.notice { padding: 14px; margin-bottom: 16px; background: var(--lx-color-primary-soft); border-radius: 6px; }.error { border-left: 3px solid var(--lx-color-danger); }.empty-state { text-align: center; padding: 32px 12px; }.empty-state p { margin: 12px 0; color: var(--lx-color-text-secondary); }
@media(max-width: 1100px) { .workspace.with-detail { grid-template-columns: minmax(0, 1fr); }.task-detail { grid-row: 1; } }
@media(max-width: 700px) { .activities { grid-template-columns: minmax(0, 1fr); }.activity, .task-section, .task-detail { padding: 16px; }.activity { gap: 7px; }.activity > .muted:first-of-type { display: none; }.activity .progress-count strong { font-size: 24px; }.filters > label { flex: 1 1 100%; min-width: 0; }input, select { max-width: 100%; }.task-toolbar { align-items: start; }.sort { width: 100%; }.sort select { flex: 1; }th, td { padding: 12px 8px; }td:first-child { min-width: 115px; }td:nth-child(2) { min-width: 130px; }td:nth-child(3) { display: none; }th:nth-child(3) { display: none; }td:last-child { min-width: 72px; }.overview-footer { align-items: start; }.refresh-line { font-size: 12px; } }
</style>
