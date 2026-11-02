<script setup lang="ts">
import { onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import VocabularyDetailDrawer from '@/components/VocabularyDetailDrawer.vue'
import { sessionGeneration } from '@/services/authState'
import { getApiError } from '@/services/apiError'
import { checkMeaningSubmissionReview, listMeaningSubmissionReviews } from '@/services/meaningSubmissionApi'
import type { MeaningSubmissionReview, ReviewQuery } from '@/services/meaningSubmissionApi'

const props = defineProps<{ visible: boolean }>()
const filters = reactive({ result: '', checked: '', applied: '' })
const page = ref(1), total = ref(0), items = ref<MeaningSubmissionReview[]>([])
const selected = ref<MeaningSubmissionReview | null>(null), wordId = ref<string | null>(null)
const loading = ref(false), checking = ref(false), error = ref(''), notice = ref('')
// An uncertain write survives list/filter/detail changes until the same record is read back.
const uncertain = ref<string | null>(null)
const session = sessionGeneration.value
let active = true, generation = 0, writeGeneration = 0
let readController: AbortController | null = null, writeController: AbortController | null = null
const live = () => active && session === sessionGeneration.value
const time = (value: string | null) => value ? new Date(value).toLocaleString() : '未核对'
function label(row: MeaningSubmissionReview) {
  if (!row.applied) return `未应用的 AI 结论（${{ accept: '原样通过', revise: '建议修改', reject: '驳回' }[row.ai.verdict]}）`
  return { accept: 'AI 原样通过', revise: 'AI 修改后通过', reject: 'AI 驳回' }[row.ai.verdict]
}
function query(): ReviewQuery {
  // The three outcome shortcuts include only decisions that actually took effect.
  return { verdict: filters.result || undefined, checked: filters.checked === '' ? undefined : filters.checked === 'true',
    applied: filters.applied !== '' ? filters.applied === 'true' : filters.result ? true : undefined }
}
function cancelRead() { generation++; readController?.abort(); loading.value = false }
function cancelWrite() { writeGeneration++; writeController?.abort(); checking.value = false }
async function confirmUnknown(signal: AbortSignal, current: () => boolean) {
  const id = uncertain.value
  if (!id) return
  const result = await listMeaningSubmissionReviews({ recordId: id }, 1, { signal, lxIsCurrent: current })
  if (!current() || uncertain.value !== id) return
  const found = result.items.find(row => row.recordId === id)
  if (!found) { notice.value = '原审核记录不存在，未重复提交。'; uncertain.value = null; return }
  if (found.checked) { notice.value = `已回读确认核对成功（${time(found.checkedAt)}）。`; uncertain.value = null }
  else { notice.value = '已回读确认尚未核对，可重新选择该记录并标记。'; uncertain.value = null }
  if (selected.value?.recordId === id) selected.value = found
}
async function load() {
  if (!live() || !props.visible) return
  cancelRead()
  const version = generation, current = () => live() && version === generation
  readController = new AbortController(); const signal = readController.signal
  loading.value = true; error.value = ''
  try {
    await confirmUnknown(signal, current)
    if (!current()) return
    const result = await listMeaningSubmissionReviews(query(), page.value, { signal, lxIsCurrent: current })
    if (!current()) return
    items.value = result.items; total.value = result.totalCount
    if (page.value > 1 && !result.items.length) { page.value = Math.max(1, Math.ceil(total.value / 20)); return await load() }
    if (selected.value) selected.value = result.items.find(row => row.recordId === selected.value?.recordId) ?? null
  } catch {
    if (current()) { items.value = []; total.value = 0; error.value = uncertain.value ? '核对结果未知，读取确认失败。请刷新确认；不会自动重复提交。' : '审核记录加载失败，请重试。' }
  } finally { if (current()) loading.value = false }
}
function search() {
  cancelWrite(); selected.value = null; wordId.value = null; page.value = 1; items.value = []
  void load()
}
function select(row: MeaningSubmissionReview) {
  cancelWrite(); selected.value = row; wordId.value = null
}
async function check() {
  const row = selected.value
  if (!live() || !props.visible || !row || row.checked || checking.value || uncertain.value) return
  cancelWrite(); cancelRead()
  const version = writeGeneration, current = () => live() && version === writeGeneration && selected.value?.recordId === row.recordId
  writeController = new AbortController(); checking.value = true
  uncertain.value = row.recordId; notice.value = '正在保存核对事实；结果不明时会先读取确认。'
  try {
    const result = await checkMeaningSubmissionReview(row.recordId, { signal: writeController.signal, lxIsCurrent: current })
    if (!current()) return
    if (result.recordId !== row.recordId || typeof result.checkedAt !== 'string') throw new Error('Invalid check response')
    uncertain.value = null; notice.value = '已标记核对。'
    selected.value = { ...row, checked: true, checkedAt: result.checkedAt }
  } catch (failure) {
    if (!current()) return
    const status = getApiError(failure).status
    if (status && status >= 400 && status < 500) {
      uncertain.value = null; notice.value = status === 404 ? '审核记录不存在。' : '核对未获接受，请刷新或检查权限。'
    } else notice.value = '核对结果未知，正在读取同一记录确认；不会自动重复提交。'
  } finally {
    if (current()) { checking.value = false; await load() }
  }
}
function changePage(delta: number) { cancelWrite(); selected.value = null; page.value += delta; void load() }
function stop() { active = false; cancelRead(); cancelWrite(); items.value = []; selected.value = null; uncertain.value = null; wordId.value = null }
watch(sessionGeneration, stop, { flush: 'sync' })
onBeforeUnmount(stop)
watch(() => props.visible, visible => { cancelRead(); cancelWrite(); if (visible) void load() })
onMounted(load)
</script>

<template>
  <section aria-label="永久 AI 审核记录">
    <p>每条有效 AI 结论永久保留；未应用的结论也可查。核对只记录管理员已查看，不会再次审核或修改词库。</p>
    <div class="review-filters">
      <label>AI 结果<select v-model="filters.result" aria-label="AI 结果" @change="search"><option value="">全部结论</option><option value="revise">AI 修改后通过</option><option value="accept">AI 原样通过</option><option value="reject">AI 驳回</option></select></label>
      <label>核对状态<select v-model="filters.checked" aria-label="核对状态" @change="search"><option value="">全部</option><option value="false">未核对</option><option value="true">已核对</option></select></label>
      <label>实际应用<select v-model="filters.applied" aria-label="实际应用" @change="search"><option value="">随结果筛选</option><option value="true">已应用</option><option value="false">未应用</option></select></label>
      <button type="button" :disabled="loading || checking" @click="load">刷新审核记录</button>
    </div>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="uncertain">核对记录 {{ uncertain }} 的结果尚未确认，请刷新确认。</p>
    <p v-if="error" role="alert">{{ error }} <button @click="load">重试读取记录</button></p>
    <p role="status">{{ loading ? '正在读取审核记录…' : `共 ${total} 条审核记录` }}</p>
    <div class="review-table">
      <table>
        <thead><tr><th>单词与原释义</th><th>AI 结果</th><th>模型与时间</th><th>核对</th><th>操作</th></tr></thead>
        <tbody><tr v-for="row in items" :key="row.recordId"><td>{{ row.original.word }} · {{ row.original.meaning }}</td><td>{{ label(row) }}</td><td>{{ row.ai.model }} · {{ time(row.ai.reviewedAt) }}</td><td>{{ row.checked ? '已核对' : '未核对' }}</td><td><button @click="select(row)">对照记录</button></td></tr></tbody>
      </table>
    </div>
    <p v-if="!loading && !error && !items.length">没有符合筛选条件的审核记录。</p>
    <nav aria-label="审核记录分页"><button :disabled="page <= 1 || loading" @click="changePage(-1)">上一页记录</button> 第 {{ page }} 页 <button :disabled="page * 20 >= total || loading" @click="changePage(1)">下一页记录</button></nav>
    <section v-if="selected" aria-label="审核记录对照">
      <h3>{{ label(selected) }}</h3>
      <p>审核人：AI；投稿来源：{{ selected.source }}；记录：{{ selected.recordId }}</p>
      <div class="review-comparison">
        <article><h4>提交原文</h4><p>单词：{{ selected.original.word }}</p><p>词性：{{ selected.original.partOfSpeech ?? '未填' }}</p><p>释义：{{ selected.original.meaning }}</p><p>例句：{{ selected.original.example ?? '未填' }}</p></article>
        <article><h4>AI 当次结论</h4><p>词性：{{ selected.ai.partOfSpeech ?? '未提供' }}</p><p>释义：{{ selected.ai.meaning ?? '未提供' }}</p><p>英式音标：{{ selected.ai.phoneticUk ?? '未提供' }}</p><p>美式音标：{{ selected.ai.phoneticUs ?? '未提供' }}</p><p>说明：{{ selected.ai.comment ?? '无' }}</p></article>
      </div>
      <p>模型：{{ selected.ai.model }}；审核时间：{{ time(selected.ai.reviewedAt) }}；实际应用：{{ selected.applied ? '已应用' : '未应用，投稿或资格已变化，未改变词库' }}。</p>
      <p>accept 保留提交内容，只采用缺失的默认音标；AI 返回的字段是历史建议，不代表覆盖了已有词库字段。</p>
      <p>核对状态：{{ selected.checked ? `已核对（${time(selected.checkedAt)}）` : '未核对' }}</p>
      <button :disabled="selected.checked || checking || !!uncertain" @click="check">{{ checking ? '正在保存核对…' : '标记已核对' }}</button>
      <button v-if="selected.resultWordId" @click="wordId = selected.resultWordId">查看词条并更正</button>
      <RouterLink to="/vocabulary">在词库查找并更正</RouterLink>
      <p>发现错误请编辑词库；修改释义文字会让已经在学这条释义的学习者重新学一次。更正或删除词条不会改写本条历史，不提供撤销或回滚。</p>
    </section>
    <VocabularyDetailDrawer v-model="wordId" />
  </section>
</template>

<style scoped>
.review-filters, .review-comparison { display: flex; flex-wrap: wrap; gap: var(--lx-space-4); margin: var(--lx-space-4) 0; }
label { display: flex; flex-direction: column; gap: var(--lx-space-2); }
button, select { min-height: 36px; padding: var(--lx-space-2); border: 1px solid var(--lx-color-border); border-radius: var(--lx-radius-sm); background: var(--lx-color-bg-surface); color: var(--lx-color-text-primary); }
button { cursor: pointer; } button:disabled { opacity: .55; cursor: default; }
button:focus-visible, select:focus-visible { outline: 2px solid var(--lx-color-focus); outline-offset: 2px; }
.review-table { overflow-x: auto; } table { border-collapse: collapse; width: 100%; } th, td { padding: var(--lx-space-3); text-align: left; border-bottom: 1px solid var(--lx-color-border-light); min-width: 110px; }
article { flex: 1; min-width: 220px; } p, td { overflow-wrap: anywhere; } section[aria-label='审核记录对照'] { margin-top: var(--lx-space-4); border-top: 1px solid var(--lx-color-border); }
</style>
