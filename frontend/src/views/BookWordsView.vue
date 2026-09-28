<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getAdminBookContent, getAdminUnitContent } from '@/services/adminVocabularyApi'
import type {
  AdminCleanupResult,
  AdminCleanupSelection,
  AdminEntryKindCounts,
  AdminSectionCounts,
  AdminUnitContent,
  AdminUnitEntryKindFilter,
  AdminUnitSectionFilter,
  AdminWordDetail
} from '@/services/adminVocabularyApi'
import { getBookUnits } from '@/services/bookApi'
import type { Book, BookUnit } from '@/types'
import { getApiError } from '@/services/apiError'
import PageHeader from '@/components/PageHeader.vue'
import VocabularyDetailDrawer from '@/components/VocabularyDetailDrawer.vue'
import VocabularyCleanupDialog from '@/components/VocabularyCleanupDialog.vue'

/**
 * One book's word list, reached from 教材管理's 查看单词. Keyword, page, and
 * size run on the server; `wordCount`/`meaningCount` count the whole book and
 * stay put while a keyword narrows the page. The route parameter identifies
 * the current target: switching books (or leaving the page) aborts the
 * in-flight request and invalidates its generation, so a late answer cannot
 * paint the book that replaced it. A disabled book is maintained the same way
 * as an enabled one.
 *
 * A unit picker narrows the same page to one unit of the book, served by the
 * unit content read with unit-scoped counts; the whole-book view and its
 * behaviour are unchanged. In a unit view a second picker narrows further to
 * one section's places of the unit — Section A, Section B, or the unsectioned
 * ones — through the same read's `section` parameter, and a third to one
 * entry kind's places — word, phrase, or the unclassified ones — through its
 * `entryKind` parameter; the two combine by intersection, the counts follow
 * the narrowing while the per-section and per-kind place counts beside them
 * always speak for the whole unit. The unit choice is page state like the
 * keyword — it never enters the route — and is reset by a book switch, and
 * the section and kind choices by a unit or book switch. Unit switches
 * share the content request's generation and aborting, so an answer for a
 * replaced unit or book cannot paint the current view. A unit deleted while
 * its view is open answers 404 and gets its own notice with a way back to
 * the whole book; the removal entries are disabled there, because the whole
 * book, not the unit, is what they remove.
 *
 * Removals take the current page's rows only: the checkbox selection lives in
 * page memory, is capped by the page size (the cleanup API accepts at most one
 * hundred word ids), and is dropped whenever the page, the keyword, or the book
 * changes — a selection never silently grows into the whole book. Every
 * removal goes through the shared preview-and-confirm dialog, and a page
 * emptied by one falls back to the last valid page.
 */

const route = useRoute()
const router = useRouter()

const bookId = computed(() => String(route.params.bookId ?? ''))

const book = ref<Book | null>(null)
const wordCount = ref(0)
const meaningCount = ref(0)
const items = ref<AdminWordDetail[]>([])
const totalCount = ref(0)

const keyword = ref('')
const searchedKeyword = ref('')
const page = ref(1)
const size = ref(20)
const pageSizes = [10, 20, 50, 100]

const units = ref<BookUnit[]>([])
const unitsLoading = ref(false)
const unitsError = ref('')
/**
 * The picker's value: the whole book by default, a unit's id once picked.
 * A sentinel rather than '', because an empty value is how the select says
 * nothing is chosen.
 */
const WHOLE_BOOK = '__whole__'
const unitId = ref(WHOLE_BOOK)
/** A unit view that answered 404: the unit (or its book) is gone. */
const unitGone = ref(false)

/**
 * The section picker's value, a unit-view-only refinement of the unit content
 * read: every place of the unit by default, or one section's (or the
 * unsectioned) places. Like the unit choice it is page state and never enters
 * the route; it resets on every unit or book switch because it belongs to the
 * view it narrowed.
 */
const ALL_SECTIONS = 'all'
const sectionFilter = ref<typeof ALL_SECTIONS | AdminUnitSectionFilter>(ALL_SECTIONS)
/** The unit's per-section place counts, reported by the unit content read whatever the filter. */
const sectionCounts = ref<AdminSectionCounts>({ sectionA: 0, sectionB: 0, noSection: 0 })

/**
 * The entry-kind picker's value, a second unit-view-only refinement of the
 * same read: every kind of the narrowed places by default, or the word or
 * phrase places, or the unclassified ones. It combines with the section
 * choice as two independent dimensions of one position; it is page state too
 * and resets on every unit or book switch.
 */
const ALL_KINDS = 'all'
const kindFilter = ref<typeof ALL_KINDS | AdminUnitEntryKindFilter>(ALL_KINDS)
/** The unit's per-kind place counts, reported by the unit content read whatever the filter. */
const entryKindCounts = ref<AdminEntryKindCounts>({ word: 0, phrase: 0, none: 0 })

const loading = ref(false)
const loadError = ref('')
const notFound = ref(false)

/** The current page's checked rows, by word id; never spans pages or filters. */
const selectedWordIds = ref<string[]>([])

const detailWordId = ref<string | null>(null)
const detailTrigger = ref<HTMLButtonElement>()

const cleanupOpen = ref(false)
const cleanupSelection = ref<AdminCleanupSelection | null>(null)

let generation = 0
let controller: AbortController | null = null

let unitsGeneration = 0
let unitsController: AbortController | null = null

const isUnitView = computed(() => unitId.value !== WHOLE_BOOK)
const currentUnit = computed(() => units.value.find((unit) => unit.id === unitId.value))

function load() {
  if (!bookId.value) {
    return
  }

  generation += 1
  const current = generation
  controller?.abort()
  controller = new AbortController()

  loading.value = true
  searchedKeyword.value = keyword.value

  const params = {
    keyword: keyword.value.trim() || undefined,
    page: page.value,
    size: size.value
  }
  const section = sectionFilter.value === ALL_SECTIONS ? undefined : { section: sectionFilter.value }
  const entryKind = kindFilter.value === ALL_KINDS ? undefined : { entryKind: kindFilter.value }
  const request = isUnitView.value
    ? getAdminUnitContent(bookId.value, unitId.value, { ...params, ...section, ...entryKind }, { signal: controller.signal })
    : getAdminBookContent(bookId.value, params, { signal: controller.signal })

  request.then(
    (data) => {
      if (current !== generation) {
        return
      }

      book.value = data.book
      wordCount.value = data.wordCount
      meaningCount.value = data.meaningCount
      // Only the unit read reports section and kind counts; the whole-book
      // view keeps zeros.
      sectionCounts.value = (data as AdminUnitContent).sectionCounts
        ?? { sectionA: 0, sectionB: 0, noSection: 0 }
      entryKindCounts.value = (data as AdminUnitContent).entryKindCounts
        ?? { word: 0, phrase: 0, none: 0 }
      items.value = data.items
      totalCount.value = data.totalCount
      // A new page starts unselected, whatever the table re-renders.
      selectedWordIds.value = []
      loading.value = false
      loadError.value = ''
      notFound.value = false
      unitGone.value = false
      // A removal can empty the page that held it; fall back to the last
      // valid page instead of showing a page beyond the end.
      if (data.totalPage > 0 && page.value > data.totalPage) {
        page.value = data.totalPage
        load()
      }
    },
    (error: unknown) => {
      if (current !== generation) {
        return
      }

      loading.value = false
      const apiError = getApiError(error)
      if (apiError.status === 404) {
        if (isUnitView.value) {
          // The unit — or the book behind it — is gone; the whole book is the
          // way back, and it answers for itself when it is the one missing.
          unitGone.value = true
          items.value = []
          selectedWordIds.value = []
          return
        }

        notFound.value = true
      } else {
        ElMessage.error(apiError.message)
        loadError.value = apiError.message
      }
    }
  )
}

/**
 * The unit picker's list, in unit order. Its own generation and abort keep a
 * book switch's answer from painting the next book's units; a failure keeps
 * the whole-book view working and offers a retry, because a picker without
 * its list cannot name a unit.
 */
function loadUnits() {
  if (!bookId.value) {
    return
  }

  unitsGeneration += 1
  const current = unitsGeneration
  unitsController?.abort()
  unitsController = new AbortController()

  unitsLoading.value = true
  unitsError.value = ''
  getBookUnits(bookId.value, { signal: unitsController.signal }).then(
    (data) => {
      if (current !== unitsGeneration) {
        return
      }

      units.value = data.units
      unitsLoading.value = false
      // The picked unit can have been deleted meanwhile; the view falls back
      // to the whole book rather than asking for a unit that no longer is.
      if (isUnitView.value && !data.units.some((unit) => unit.id === unitId.value)) {
        unitId.value = WHOLE_BOOK
      }
    },
    (error: unknown) => {
      if (current !== unitsGeneration) {
        return
      }

      unitsLoading.value = false
      const apiError = getApiError(error)
      ElMessage.error(apiError.message)
      unitsError.value = apiError.message
    }
  )
}

function handleSelectionChange(rows: AdminWordDetail[]) {
  selectedWordIds.value = rows.map((row) => row.id)
}

function openRemoveWords(wordIds: string[]) {
  if (!wordIds.length) {
    return
  }
  if (wordIds.length > 100) {
    ElMessage.warning('一次最多移除 100 个单词；请缩小当前页或减少选择')
    return
  }

  cleanupSelection.value = { action: 'removeWords', wordIds }
  cleanupOpen.value = true
}

function handleCleanupCommitted(result: AdminCleanupResult) {
  ElMessage.success(
    `已移除：删除释义 ${result.deletedMeaningCount} 条，${result.deletedWordCount} 个单词失去全部教材引用一并删除`
  )
  if (result.deletedBook) {
    void router.replace({ name: 'books' })
    return
  }

  load()
  loadUnits()
}

function handleCleanupUnknown() {
  // The commit's answer never arrived: re-query the current page, which also
  // applies the last-valid-page fallback, instead of replaying the removal.
  load()
}

watch(bookId, () => {
  keyword.value = ''
  page.value = 1
  book.value = null
  wordCount.value = 0
  meaningCount.value = 0
  sectionCounts.value = { sectionA: 0, sectionB: 0, noSection: 0 }
  entryKindCounts.value = { word: 0, phrase: 0, none: 0 }
  items.value = []
  totalCount.value = 0
  selectedWordIds.value = []
  detailWordId.value = null
  unitId.value = WHOLE_BOOK
  sectionFilter.value = ALL_SECTIONS
  kindFilter.value = ALL_KINDS
  units.value = []
  unitsError.value = ''
  unitGone.value = false
  load()
  loadUnits()
})

// A unit switch is a new view of the same book: the page restarts, the section
// and kind refinements belong to the view it replaced, and the previous view's
// selection does not carry over.
watch(unitId, () => {
  page.value = 1
  sectionFilter.value = ALL_SECTIONS
  kindFilter.value = ALL_KINDS
  selectedWordIds.value = []
  load()
})

onMounted(() => {
  load()
  loadUnits()
})

onBeforeUnmount(() => {
  generation += 1
  controller?.abort()
  unitsGeneration += 1
  unitsController?.abort()
})

function handleSearch() {
  page.value = 1
  load()
}

function handleSizeChange(newSize: number) {
  size.value = newSize
  page.value = 1
  load()
}

/** A section or a kind picked in the unit view: same restart as a size change, without touching the unit. */
function handleSectionChange() {
  page.value = 1
  selectedWordIds.value = []
  load()
}

function handlePageChange(newPage: number) {
  page.value = newPage
  load()
}

function backToBooks() {
  void router.push({ name: 'books' })
}

/** Leaves a gone unit's notice for the whole-book view of the same book. */
function backToWholeBook() {
  unitId.value = WHOLE_BOOK
}

function openDetail(row: AdminWordDetail, event: Event) {
  detailTrigger.value = event.currentTarget as HTMLButtonElement
  detailWordId.value = row.id
}

function refocusDetailTrigger() {
  detailTrigger.value?.focus()
}

const emptyDescription = computed(() =>
  searchedKeyword.value
    ? '没有匹配的单词'
    : isUnitView.value
      ? '该单元暂无单词'
      : '该教材暂无单词'
)
</script>

<template>
  <div class="book-words-view">
    <PageHeader
      title="教材词表"
      :description="book ? `${book.bookName} 的单词与释义` : '正在加载教材信息…'"
    >
      <template #actions>
        <el-button @click="backToBooks">返回教材列表</el-button>
      </template>
    </PageHeader>

    <section class="words-panel">
      <el-alert
        v-if="notFound"
        class="book-words__notice"
        type="warning"
        title="教材不存在或已被删除"
        :closable="false"
        show-icon
      >
        <el-button size="small" @click="backToBooks">返回教材列表</el-button>
      </el-alert>

      <el-alert
        v-else-if="unitGone"
        class="book-words__notice"
        type="warning"
        title="该单元不存在或已被删除"
        :closable="false"
        show-icon
      >
        <el-button size="small" @click="backToWholeBook">返回全书</el-button>
        <el-button size="small" @click="backToBooks">返回教材列表</el-button>
      </el-alert>

      <template v-else>
        <p v-if="book" class="book-words__meta">
          <el-tag :type="book.status ? 'success' : 'info'" size="small">
            {{ book.status ? '启用' : '停用' }}
          </el-tag>
          <el-tag v-if="currentUnit" size="small" type="warning">
            单元 {{ currentUnit.number }}{{ currentUnit.title ? ` · ${currentUnit.title}` : '' }}
          </el-tag>
          <span>去重单词 {{ wordCount }}</span>
          <span>释义 {{ meaningCount }}</span>
          <span v-if="isUnitView" class="book-words__sections">
            分节 A {{ sectionCounts.sectionA }} · B {{ sectionCounts.sectionB }} · 未分节 {{ sectionCounts.noSection }}
          </span>
          <span v-if="isUnitView" class="book-words__kinds">
            单词 {{ entryKindCounts.word }} · 短语 {{ entryKindCounts.phrase }} · 未分类 {{ entryKindCounts.none }}
          </span>
        </p>

        <el-alert
          v-if="unitsError"
          class="book-words__error"
          type="error"
          :title="`单元列表加载失败：${unitsError}`"
          :closable="false"
          show-icon
        >
          <el-button class="book-words__retry" size="small" @click="loadUnits">重试单元列表</el-button>
        </el-alert>

        <div class="toolbar">
          <el-select
            v-model="unitId"
            class="toolbar__unit"
            aria-label="筛选单元"
            :loading="unitsLoading"
            :disabled="!!unitsError"
          >
            <el-option label="全书" :value="WHOLE_BOOK" />
            <el-option
              v-for="unit in units"
              :key="unit.id"
              :value="unit.id"
              :label="unit.title ? `单元 ${unit.number} · ${unit.title}` : `单元 ${unit.number}`"
            />
          </el-select>
          <el-select
            v-if="isUnitView"
            v-model="sectionFilter"
            class="toolbar__section"
            aria-label="筛选分节"
            @change="handleSectionChange"
          >
            <el-option label="全部分节" :value="ALL_SECTIONS" />
            <el-option label="Section A" value="A" />
            <el-option label="Section B" value="B" />
            <el-option label="未分节" value="none" />
          </el-select>
          <el-select
            v-if="isUnitView"
            v-model="kindFilter"
            class="toolbar__kind"
            aria-label="筛选类别"
            @change="handleSectionChange"
          >
            <el-option label="全部类别" :value="ALL_KINDS" />
            <el-option label="单词" value="word" />
            <el-option label="短语" value="phrase" />
            <el-option label="未分类" value="none" />
          </el-select>
          <el-input
            v-model="keyword"
            class="toolbar__search"
            aria-label="搜索单词"
            placeholder="搜索单词"
            clearable
            @keyup.enter="handleSearch"
          />
          <el-button type="primary" @click="handleSearch">搜索</el-button>
          <el-tooltip
            :disabled="!isUnitView"
            content="单元视图下不能从本教材移除；请切回全书视图操作"
            placement="top"
          >
            <span class="toolbar__remove-wrap">
              <el-button
                type="danger"
                plain
                class="toolbar__remove"
                :disabled="isUnitView || !selectedWordIds.length"
                aria-label="从本教材移除选中的单词"
                @click="openRemoveWords(selectedWordIds)"
              >
                从本教材移除（{{ selectedWordIds.length }}）
              </el-button>
            </span>
          </el-tooltip>
        </div>

        <el-alert
          v-if="loadError"
          class="book-words__error"
          type="error"
          :title="loadError"
          :closable="false"
          show-icon
        >
          <el-button class="book-words__retry" size="small" @click="load">重试</el-button>
        </el-alert>

        <el-table
          v-loading="loading"
          :data="items"
          row-key="id"
          stripe
          border
          scrollbar-tabindex="0"
          @selection-change="handleSelectionChange"
        >
          <el-table-column type="selection" width="44" />
          <el-table-column prop="word" label="单词" min-width="140" />
          <el-table-column label="英式音标" min-width="130">
            <template #default="{ row }">{{ row.phoneticUk ?? '—' }}</template>
          </el-table-column>
          <el-table-column label="美式音标" min-width="130">
            <template #default="{ row }">{{ row.phoneticUs ?? '—' }}</template>
          </el-table-column>
          <el-table-column :label="isUnitView ? '本单元释义' : '本教材释义'" min-width="260">
            <template #default="{ row }">
              <ul v-if="row.meanings.length" class="book-words__meanings">
                <li v-for="meaning in row.meanings" :key="meaning.id">
                  <el-tag v-if="meaning.partOfSpeech" size="small" type="primary">
                    {{ meaning.partOfSpeech }}
                  </el-tag>
                  <span>{{ meaning.meaning }}</span>
                </li>
              </ul>
              <span v-else>—</span>
            </template>
          </el-table-column>
          <el-table-column label="操作" width="130" fixed="right">
            <template #default="{ row }">
              <el-button link type="primary" @click="openDetail(row, $event)">详情</el-button>
              <el-tooltip
                :disabled="!isUnitView"
                content="单元视图下不能从本教材移除；请切回全书视图操作"
                placement="top"
              >
                <span class="book-words__remove-wrap">
                  <el-button link type="danger" :disabled="isUnitView" @click="openRemoveWords([row.id])">
                    移除
                  </el-button>
                </span>
              </el-tooltip>
            </template>
          </el-table-column>
          <template #empty>
            <el-empty v-if="!loadError" :image-size="80" :description="emptyDescription" />
          </template>
        </el-table>

        <div class="pagination-wrapper">
          <el-select
            v-model="size"
            class="page-size"
            aria-label="每页条数"
            @change="handleSizeChange"
          >
            <el-option
              v-for="option in pageSizes"
              :key="option"
              :label="`${option} 条/页`"
              :value="option"
            />
          </el-select>
          <el-pagination
            v-model:current-page="page"
            v-model:page-size="size"
            :total="totalCount"
            layout="total, prev, pager, next, jumper"
            background
            @size-change="handleSizeChange"
            @current-change="handlePageChange"
          />
        </div>
      </template>
    </section>

    <VocabularyDetailDrawer
      v-model="detailWordId"
      @closed="refocusDetailTrigger"
      @changed="load"
    />

    <VocabularyCleanupDialog
      v-model="cleanupOpen"
      :book-id="bookId"
      :selection="cleanupSelection"
      @committed="handleCleanupCommitted"
      @unknown="handleCleanupUnknown"
    />
  </div>
</template>

<style scoped>
.words-panel {
  padding: var(--lx-space-4);
  border: 1px solid var(--lx-color-border-light);
  border-radius: var(--lx-radius-md);
  background: var(--lx-color-bg-surface);
  box-shadow: var(--lx-shadow-1);
}

.book-words__notice {
  margin-bottom: var(--lx-space-4);
}

.book-words__meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--lx-space-3);
  margin: 0 0 var(--lx-space-4);
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-sm);
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--lx-space-3);
  margin-bottom: var(--lx-space-4);
}
.toolbar__search {
  width: min(320px, 100%);
}
.toolbar__unit,
.toolbar__section {
  width: min(220px, 100%);
}
.toolbar__section {
  width: min(150px, 100%);
}
.toolbar__kind {
  width: min(150px, 100%);
}
.toolbar__remove-wrap,
.book-words__remove-wrap {
  display: inline-flex;
}

.book-words__error {
  margin-bottom: var(--lx-space-4);
}
.book-words__retry {
  margin-top: var(--lx-space-2);
}

.book-words__meanings {
  margin: 0;
  padding: 0;
  list-style: none;
}
.book-words__meanings li {
  display: flex;
  align-items: baseline;
  gap: var(--lx-space-2);
}
.book-words__meanings li + li {
  margin-top: var(--lx-space-1);
}

.pagination-wrapper {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: var(--lx-space-3);
  margin-top: var(--lx-space-4);
}
.page-size {
  width: 120px;
}
</style>
