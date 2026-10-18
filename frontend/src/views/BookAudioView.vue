<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import TextbookAudioUpload from '@/components/TextbookAudioUpload.vue'
import PageHeader from '@/components/PageHeader.vue'
import { getAdminBookContent } from '@/services/adminVocabularyApi'
import { getBookUnits } from '@/services/bookApi'
import { getApiError } from '@/services/apiError'
import { sessionGeneration } from '@/services/authState'
import { createTextbookAudioPreview, textbookAudioTargetKey } from '@/services/textbookAudioPreview'
import { createTextbookAudioTrack, getTextbookAudioTracks, removeTextbookAudioTrack, replaceTextbookAudioTrack } from '@/services/textbookAudioApi'
import type { TextbookAudioTrack } from '@/services/textbookAudioApi'
import type { Book, BookUnit } from '@/types'

const uploader = ref<InstanceType<typeof TextbookAudioUpload>>()
const preview = createTextbookAudioPreview()
const isPreviewing = (trackId: string) => preview.state.target === textbookAudioTargetKey(bookId.value, unitId.value, trackId) && preview.state.phase !== 'idle'
const route = useRoute()
const router = useRouter()
const bookId = computed(() => String(route.params.bookId ?? ''))
const book = ref<Book | null>(null)
const units = ref<BookUnit[]>([])
const unitId = ref('')
const unitGone = ref(false)
const page = ref(1)
const size = ref(20)
const items = ref<TextbookAudioTrack[]>([])
const totalCount = ref(0)
const contextLoading = ref(false)
const contextError = ref('')
const loading = ref(false)
const loadError = ref('')
const notice = ref('')
const busy = ref(false)
const dialog = ref(false)
const mode = ref<'create' | 'edit' | 'delete'>('create')
const selected = ref<TextbookAudioTrack | null>(null)
const title = ref('')
const order = ref<number | undefined>(0)
const available = ref(false)
const draftError = ref('')
const needsReview = ref(false)
const createReviewed = ref(false)
const trigger = ref<HTMLElement | null>(null)
const createButton = ref<{ $el: HTMLButtonElement }>()
const reloadButton = ref<{ $el: HTMLButtonElement }>()
const latest = computed(() => items.value.find(item => item.id === selected.value?.id))
const valid = computed(() => title.value.trim().length >= 1 && title.value.trim().length <= 200
  && Number.isInteger(order.value) && order.value! >= 0 && order.value! <= 2147483647)

// A scope change invalidates reads, writes and their finally blocks immediately.
// Each request also belongs to exactly one authentication generation, even if
// the next administrator signs in with the same username.
let generation = 0
let readGeneration = 0
let contextController: AbortController | null = null
let readController: AbortController | null = null
let writeController: AbortController | null = null
let disposed = false
function request(controller: AbortController) {
  const ownGeneration = generation
  const ownSession = sessionGeneration.value
  const owns = () => !disposed && generation === ownGeneration && sessionGeneration.value === ownSession
  return { owns, config: { signal: controller.signal, lxIsCurrent: () => owns() && !controller.signal.aborted } }
}
function invalidate(clearTotal = true) {
  preview.stop()
  uploader.value?.invalidate()
  generation += 1
  readGeneration += 1
  contextController?.abort()
  readController?.abort()
  writeController?.abort()
  busy.value = false
  loading.value = false
  contextLoading.value = false
  dialog.value = false
  selected.value = null
  title.value = ''
  order.value = 0
  available.value = false
  createReviewed.value = false
  draftError.value = ''
  needsReview.value = false
  notice.value = ''
  items.value = []
  if (clearTotal) totalCount.value = 0
  loadError.value = ''
  unitGone.value = false
}
async function loadContext() {
  preview.stop()
  contextController?.abort()
  const controller = new AbortController()
  contextController = controller
  const { config, owns } = request(controller)
  const current = () => owns() && contextController === controller && !controller.signal.aborted
  contextLoading.value = true
  contextError.value = ''
  try {
    // These existing helpers expect an already encoded component.
    const encodedBook = encodeURIComponent(bookId.value)
    const [content, data] = await Promise.all([
      getAdminBookContent(encodedBook, { page: 1, size: 1 }, config),
      getBookUnits(encodedBook, config)
    ])
    if (!current()) return
    book.value = content.book
    units.value = [...data.units].sort((a, b) => a.number - b.number)
    if (!unitId.value) {
      contextLoading.value = false
      changeUnit(units.value[0]?.id ?? '')
    } else {
      // Re-reading after a 404 must preserve the original target and draft.
      // Only an explicit unit choice may discard them, even when another
      // valid unit is available. Never silently move a write to that unit.
      unitGone.value = !units.value.some(unit => unit.id === unitId.value)
      if (unitGone.value) items.value = []
    }
  } catch (error) {
    if (!current()) return
    const failure = getApiError(error)
    contextError.value = failure.status === 404 ? '教材或单元不存在，请返回教材列表核对。' : `教材与单元读取失败：${failure.message}`
  } finally {
    if (current()) contextLoading.value = false
  }
}
async function load(): Promise<boolean> {
  if (!unitId.value) return false
  preview.stop()
  readController?.abort()
  const controller = new AbortController()
  readController = controller
  const read = ++readGeneration
  const { config, owns } = request(controller)
  const current = () => owns() && read === readGeneration && !controller.signal.aborted
  loading.value = true
  loadError.value = ''
  try {
    const data = await getTextbookAudioTracks(bookId.value, unitId.value, { page: page.value, size: size.value }, config)
    if (!current()) return false
    const lastPage = Math.max(1, data.totalPage)
    if (page.value > lastPage) {
      // A read-back must not silently abandon an unknown content attempt by
      // navigating away from its original page. Confirmation or explicit
      // abandonment releases it; a later read can then use the normal fallback.
      if (uploader.value?.hasAttempt) {
        loadError.value = '目录页码已变化，原上传仍待核对。请先核对原上传结果或放弃继续核对，再重新读取目录。'
        return false
      }
      page.value = lastPage
      return await load()
    }
    items.value = data.items
    totalCount.value = data.totalCount
    uploader.value?.catalogRefreshed()
    return true
  } catch (error) {
    if (!current()) return false
    const failure = getApiError(error)
    loadError.value = failure.status === 404 ? '教材、单元或录音已不存在，请重新读取单元并核对。' : `目录读取失败：${failure.message}`
    if (failure.status === 404) void loadContext()
    return false
  } finally {
    if (current()) loading.value = false
  }
}
async function changeUnit(value: string) {
  if (value === unitId.value || !await releaseUpload()) return
  invalidate()
  unitId.value = value
  page.value = 1
  void load()
}
async function changePage(value: number) {
  if (page.value === value || !await releaseUpload()) return
  invalidate(false)
  page.value = value
  void load()
}
async function changeSize(value: number) {
  if (size.value === value || !await releaseUpload()) return
  invalidate(false)
  size.value = value
  page.value = 1
  void load()
}
async function openEditor(kind: 'create' | 'edit' | 'delete', event: Event, track?: TextbookAudioTrack) {
  const button = event.currentTarget as HTMLElement
  if (!await releaseUpload()) return
  preview.stop()
  uploader.value?.invalidate()
  trigger.value = button
  mode.value = kind
  selected.value = track ? { ...track } : null
  title.value = track?.title ?? ''
  order.value = track?.displayOrder ?? 0
  available.value = track?.isAvailable ?? false
  draftError.value = ''
  needsReview.value = false
  createReviewed.value = false
  dialog.value = true
}
function useCurrentVersion() {
  if (!latest.value) return
  selected.value = { ...latest.value }
  needsReview.value = false
  draftError.value = '已明确采用当前版本条件；草稿标题、顺序和可用选择保留，请核对后提交。'
}
async function refocus() {
  await nextTick()
  if (disposed) return
  if (trigger.value?.isConnected) trigger.value.focus()
  else if (createButton.value && !createButton.value.$el.disabled) createButton.value.$el.focus()
  else reloadButton.value?.$el.focus()
}
function beforeClose(done: () => void) {
  if (!busy.value) done()
}
async function submit() {
  if (busy.value || !unitId.value || unitGone.value || (mode.value !== 'delete' && !valid.value)) return
  if (needsReview.value && !(mode.value === 'create' && createReviewed.value)) return
  preview.stop()
  busy.value = true
  draftError.value = ''
  writeController = new AbortController()
  const controller = writeController
  const { config, owns } = request(controller)
  // Capture every target/condition before dispatch; read-back never silently
  // changes the version of a retained draft after a conflict or unknown write.
  const target = { book: bookId.value, unit: unitId.value, track: selected.value ? { ...selected.value } : null, mode: mode.value }
  try {
    if (target.mode === 'create') {
      await createTextbookAudioTrack(target.book, target.unit, { title: title.value.trim(), displayOrder: order.value! }, config)
    } else if (target.track) {
      if (target.mode === 'delete') await removeTextbookAudioTrack(target.book, target.unit, target.track.id, target.track.version, config)
      else await replaceTextbookAudioTrack(target.book, target.unit, target.track.id, {
        title: title.value.trim(), displayOrder: order.value!, isAvailable: available.value, expectedVersion: target.track.version
      }, config)
    }
    if (!owns()) return
    notice.value = '写操作已确认。'
    dialog.value = false
    const refreshed = await load()
    if (!owns()) return
    notice.value = refreshed ? '写操作已确认，目录已重新读取。' : '写操作已确认，但当前目录读取失败；请重试读取，不要重复写入。'
  } catch (error) {
    if (!owns()) return
    const failure = getApiError(error)
    const unknown = controller.signal.aborted || failure.status === undefined || failure.status >= 500
    needsReview.value = unknown || failure.status === 409 || failure.status === 404
    createReviewed.value = false
    draftError.value = unknown
      ? '结果未知，服务器可能已完成。草稿与原版本保留；请对照重新读取的目录，不会自动重发。取消请求不能撤销服务器结果。'
      : failure.status === 409
        ? '版本或文件状态冲突。草稿与原版本保留，请对照当前目录，再明确选择是否采用当前版本。'
        : failure.status === 404
          ? '目标已不存在。草稿保留，请核对当前目录与单元。'
          : `请求被拒绝（${failure.status ?? '网络'}）：${failure.message}`
    notice.value = draftError.value
    // Reads are independent of the canceled write signal.
    if (needsReview.value) {
      await load()
      if (owns() && failure.status === 404) void loadContext()
    }
  } finally {
    if (owns() && writeController === controller) busy.value = false
  }
}
// Route guards ask before the target changes. Logout clears the attempt
// synchronously and never waits for a confirmation.
async function releaseUpload() {
  const own = generation
  const session = sessionGeneration.value
  const allowed = !uploader.value || await uploader.value.confirmLeave()
  return allowed && own === generation && session === sessionGeneration.value
}
async function allowRouteChange() {
  // Actual book watch/unmount performs cleanup after navigation succeeds.
  const allowed = await releaseUpload()
  if (allowed) preview.stop()
  return allowed
}
async function togglePreview(track: TextbookAudioTrack) {
  if (isPreviewing(track.id)) { preview.stop(); return }
  const own = generation
  const session = sessionGeneration.value
  if (uploader.value?.hasAttempt && uploader.value.targetId() !== track.id) {
    if (!await releaseUpload()) return
    uploader.value.invalidate()
    if (!await load()) return
  }
  if (disposed || own !== generation || session !== sessionGeneration.value || !uploader.value?.canStart) return
  const current = items.value.find(item => item.id === track.id)
  if (!current?.hasFile || busy.value || loading.value || (uploader.value.sending && uploader.value.targetId() === track.id)) return
  await preview.play(bookId.value, unitId.value, track.id)
}
watch(() => uploader.value?.canStart, enabled => { if (!enabled) preview.stop() }, { flush: 'sync' })
onBeforeRouteLeave(allowRouteChange)
onBeforeRouteUpdate(allowRouteChange)
watch(bookId, () => {
  invalidate()
  book.value = null
  units.value = []
  unitId.value = ''
  page.value = 1
  contextError.value = ''
  void loadContext()
}, { immediate: true, flush: 'sync' })
watch(sessionGeneration, () => {
  invalidate()
  contextLoading.value = false
  contextError.value = ''
  book.value = null
  units.value = []
  unitId.value = ''
}, { flush: 'sync' })
onBeforeUnmount(() => { disposed = true; invalidate() })
</script>

<template>
  <div class="book-audio-view">
    <PageHeader title="教材录音" :description="book ? `${book.bookName} · 单元录音目录` : '读取教材与单元信息'">
      <template #actions>
        <el-button @click="router.push({ name: 'book-words', params: { bookId } })">教材词表</el-button>
        <el-button @click="router.push({ name: 'books' })">返回教材列表</el-button>
      </template>
    </PageHeader>
    <section class="audio-panel" :aria-busy="contextLoading || loading">
      <p v-if="book" class="audio-meta">{{ book.status ? '启用教材' : '禁用教材（仍可管理录音）' }} · 目录不依赖词条；文件状态不保证对象当前可读。</p>
      <p v-if="contextLoading" role="status">正在读取教材与单元…</p>
      <el-alert v-if="contextError" :title="contextError" type="error" :closable="false">
        <el-button @click="loadContext">重试教材与单元</el-button>
      </el-alert>
      <p v-if="!contextLoading && !contextError && book && !units.length && !unitGone" role="status">此教材还没有单元。请返回教材列表，在该教材的「单元」入口先建单元。</p>
      <template v-if="units.length || unitGone">
        <el-alert v-if="unitGone" title="原单元已不存在；草稿和原目标保留。请明确选择其他单元，或返回教材列表的单元入口核对。" type="warning" :closable="false" />
        <div class="audio-toolbar">
          <el-select :model-value="unitId" aria-label="录音单元" class="unit-select" @change="changeUnit">
            <el-option v-if="unitGone" :value="unitId" label="原单元（已不存在）" disabled />
            <el-option v-for="unit in units" :key="unit.id" :value="unit.id" :label="`单元 ${unit.number}${unit.title ? ` · ${unit.title}` : ''}`" />
          </el-select>
          <el-button ref="reloadButton" :disabled="loading || busy" @click="load">重新读取目录</el-button>
          <el-button ref="createButton" type="primary" :disabled="contextLoading || !!contextError || !unitId || unitGone || busy" @click="openEditor('create', $event)">新增录音</el-button>
        </div>
        <TextbookAudioUpload ref="uploader" :book-id="bookId" :unit-id="unitId" :refresh="load" :current-track="id => items.find(item => item.id === id)" @before-content-mutation="preview.stop" @after-refresh="preview.stop" @scope-invalidated="preview.stop" />
        <div class="preview-feedback" aria-live="polite">
          <p v-if="preview.state.phase !== 'idle'" role="status">{{ preview.state.phase === 'loading' ? '正在获取试听…' : '正在播放录音' }}</p>
          <p v-if="preview.state.error" role="alert">{{ preview.state.error }}</p>
        </div>
        <p class="audio-feedback" role="status" aria-live="polite">{{ notice }}</p>
        <el-alert v-if="loadError" :title="loadError" type="error" :closable="false">
          <el-button @click="load">重试目录</el-button>
        </el-alert>
        <p v-if="loading" role="status">正在读取录音目录…</p>
        <el-table v-if="!unitGone" :data="items" stripe border row-key="id" scrollbar-tabindex="0" aria-label="单元录音目录">
          <el-table-column prop="title" label="标题" min-width="200" />
          <el-table-column prop="displayOrder" label="顺序" min-width="90" />
          <el-table-column label="时长（秒）" min-width="110"><template #default="{ row }">{{ row.durationSeconds === null ? '—' : row.durationSeconds.toFixed(2) }}</template></el-table-column>
          <el-table-column label="文件" min-width="100"><template #default="{ row }">{{ row.hasFile ? '已有文件' : '缺少文件' }}</template></el-table-column>
          <el-table-column label="可用状态" min-width="100"><template #default="{ row }">{{ row.isAvailable ? '可用' : '未可用' }}</template></el-table-column>
          <el-table-column label="操作" min-width="320">
            <template #default="{ row }">
              <el-button link type="primary" :disabled="busy || loading || (uploader?.sending && uploader.targetId() === row.id)" @click="openEditor('edit', $event, row)">编辑</el-button>
              <el-button link type="primary" :disabled="busy || loading || !!loadError || !uploader?.canStart" @click="uploader?.open(row, $event)">上传文件</el-button>
              <el-button link type="primary" :aria-label="`${isPreviewing(row.id) ? '停止' : '试听'}录音 ${row.title}`" :disabled="!row.hasFile || busy || loading || !uploader?.canStart || (uploader.sending && uploader.targetId() === row.id)" @click="togglePreview(row)">{{ isPreviewing(row.id) ? '停止试听' : '试听' }}</el-button>
              <el-button link type="danger" :disabled="busy || loading || (uploader?.sending && uploader.targetId() === row.id)" @click="openEditor('delete', $event, row)">移除</el-button>
            </template>
          </el-table-column>
          <template #empty><el-empty v-if="!loading && !loadError" :image-size="70" description="本单元暂无录音，可新增录音目录" /><span v-else>{{ loading ? '正在读取' : '目录读取失败' }}</span></template>
        </el-table>
        <div v-if="!unitGone" class="audio-pagination">
          <el-select :model-value="size" class="page-size" aria-label="录音每页条数" @change="changeSize"><el-option v-for="value in [10, 20, 50, 100]" :key="value" :value="value" :label="`${value} 条/页`" /></el-select>
          <el-pagination :current-page="page" :page-size="size" :total="totalCount" layout="total, prev, pager, next" background @current-change="changePage" />
        </div>
      </template>
    </section>
    <el-dialog v-model="dialog" :title="mode === 'create' ? '新增录音' : mode === 'delete' ? '移除录音' : '编辑录音'" width="min(620px, calc(100vw - 32px))" :before-close="beforeClose" :close-on-click-modal="!busy" :close-on-press-escape="!busy" @closed="refocus">
      <template v-if="mode === 'delete'"><p>确认移除「{{ title }}」这条目录？历史文件和操作回执会保留，不会物理删除对象。</p></template>
      <el-form v-else label-position="top" @submit.prevent="submit">
        <el-form-item label="标题"><el-input v-model="title" aria-label="录音标题" :disabled="busy" /></el-form-item>
        <el-form-item label="顺序"><el-input-number v-model="order" aria-label="录音顺序" :min="0" :max="2147483647" :step="1" :precision="0" :disabled="busy" /></el-form-item>
        <p v-if="!valid" role="status">标题去除首尾空白后需为 1–200 个 UTF-16 字符；顺序需为 0–2147483647 的整数。</p>
        <el-form-item v-if="mode === 'edit'" label="可用状态"><el-checkbox v-model="available" :disabled="busy || !selected?.hasFile">设为可用</el-checkbox><span v-if="!selected?.hasFile">缺少文件，不能启用。</span></el-form-item>
      </el-form>
      <p v-if="selected" class="version-text">提交条件版本：{{ selected.version }}</p>
      <p v-if="draftError" role="alert" aria-live="assertive">{{ draftError }}</p>
      <div v-if="needsReview" class="review-draft">
        <p v-if="latest">当前目录：{{ latest.title }} · 顺序 {{ latest.displayOrder }} · {{ latest.isAvailable ? '可用' : '未可用' }} · 版本 {{ latest.version }}</p>
        <p v-else-if="mode !== 'create'">当前页未找到原录音；请关闭草稿并核对目录，不自动覆盖。</p>
        <el-button v-if="latest && mode !== 'create'" :disabled="busy || loading || !!loadError" @click="useCurrentVersion">保留草稿，采用当前版本</el-button>
        <el-checkbox v-if="mode === 'create'" v-model="createReviewed" :disabled="busy || loading || !!loadError">我已核对目录，明确重新创建（可能产生另一条录音）</el-checkbox>
      </div>
      <template #footer>
        <el-button v-if="busy" @click="writeController?.abort()">取消等待（结果可能未知）</el-button>
        <el-button v-else @click="dialog = false">取消</el-button>
        <el-button type="primary" :loading="busy" :disabled="unitGone || (mode !== 'delete' && !valid) || (needsReview && !(mode === 'create' && createReviewed))" @click="submit">{{ mode === 'delete' ? '确认移除' : '保存' }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.book-audio-view { min-width: 0; }
.book-audio-view :deep(.page-header__text), .book-audio-view :deep(.el-dialog) { min-width: 0; overflow-wrap: anywhere; }
.audio-panel { padding: var(--lx-space-4); border: 1px solid var(--lx-color-border-light); border-radius: var(--lx-radius-md); background: var(--lx-color-bg-surface); box-shadow: var(--lx-shadow-1); min-width: 0; }
.audio-meta, .version-text { color: var(--lx-color-text-secondary); overflow-wrap: anywhere; }
.audio-toolbar, .audio-pagination { display: flex; flex-wrap: wrap; align-items: center; gap: var(--lx-space-3); margin: var(--lx-space-4) 0; }
.audio-pagination { justify-content: flex-end; }
.unit-select { width: min(300px, 100%); }
.page-size { width: 130px; }
.audio-feedback { min-height: 1.5em; overflow-wrap: anywhere; }
.review-draft { overflow-wrap: anywhere; }
.review-draft :deep(.el-checkbox) { height: auto; white-space: normal; }
.review-draft :deep(.el-checkbox__label) { white-space: normal; }
</style>
