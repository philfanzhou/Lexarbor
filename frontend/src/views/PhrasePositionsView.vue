<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { getBooks, getBookUnits } from '@/services/bookApi'
import { getAdminPhrasePositions } from '@/services/adminVocabularyApi'
import type { AdminPhrasePosition, AdminUnitSectionFilter } from '@/services/adminVocabularyApi'
import type { Book, BookUnit } from '@/types'
import { getApiError } from '@/services/apiError'
import PageHeader from '@/components/PageHeader.vue'
import VocabularyDetailDrawer from '@/components/VocabularyDetailDrawer.vue'
import MeaningPositionDialog from '@/components/MeaningPositionDialog.vue'
import type { PositionTarget } from '@/components/MeaningPositionDialog.vue'

const books = ref<Book[]>([])
const booksLoading = ref(false)
const booksError = ref('')
const bookId = ref('')
const units = ref<BookUnit[]>([])
const unitsLoading = ref(false)
const unitsError = ref('')
const unitId = ref('')
const section = ref<'' | AdminUnitSectionFilter>('')
const keyword = ref('')
const submittedKeyword = ref('')
const page = ref(1)
const size = ref(20)
const items = ref<AdminPhrasePosition[]>([])
const totalCount = ref(0)
const loading = ref(false)
const error = ref('')
const detailWordId = ref<string | null>(null)
const detailTrigger = ref<HTMLButtonElement>()
const bookPicker = ref<HTMLElement>()
const detailGeneration = ref(0)
const positionTarget = ref<PositionTarget | null>(null)
let requestId = 0
let unitRequestId = 0
let controller: AbortController | null = null
let bookController: AbortController | null = null
let unitController: AbortController | null = null
let bookRequestId = 0
const bookKeyword = ref('')
const bookPage = ref(1)
const bookTotalPage = ref(1)
const bookTotalCount = ref(0)
const chosenBook = ref<Book | null>(null)
const bookOptions = computed(() => chosenBook.value && !books.value.some(book => book.id === chosenBook.value?.id)
  ? [chosenBook.value, ...books.value] : books.value)

async function loadBooks() {
  const current = ++bookRequestId
  bookController?.abort()
  bookController = new AbortController()
  booksLoading.value = true
  booksError.value = ''
  try {
    const response = await getBooks({ keyword: bookKeyword.value.trim() || undefined, page: bookPage.value, size: 20 }, { signal: bookController.signal })
    if (current !== bookRequestId) return
    books.value = response.items
    bookTotalPage.value = response.totalPage
    bookTotalCount.value = response.totalCount
  } catch (cause: unknown) {
    if (current === bookRequestId) booksError.value = getApiError(cause).message
  } finally {
    if (current === bookRequestId) booksLoading.value = false
  }
}
function searchBooks(query: string) {
  if (query === bookKeyword.value && !booksError.value && (booksLoading.value || books.value.length)) return
  bookKeyword.value = query; bookPage.value = 1; void loadBooks()
}
function turnBookPage(delta: number) { bookPage.value += delta; void loadBooks() }

async function loadUnits(selectedBook: string) {
  const current = ++unitRequestId
  unitController?.abort()
  unitController = new AbortController()
  units.value = []
  unitsError.value = ''
  unitsLoading.value = false
  if (!selectedBook) return
  unitsLoading.value = true
  try {
    const response = await getBookUnits(selectedBook, { signal: unitController.signal })
    if (current === unitRequestId) units.value = response.units
  } catch (cause: unknown) {
    if (current === unitRequestId) unitsError.value = getApiError(cause).message
  } finally {
    if (current === unitRequestId) unitsLoading.value = false
  }
}

async function load() {
  const current = ++requestId
  controller?.abort()
  controller = new AbortController()
  items.value = []
  error.value = ''
  if (!bookId.value) {
    totalCount.value = 0
    loading.value = false
    return
  }
  loading.value = true
  try {
    const response = await getAdminPhrasePositions(bookId.value, {
      unitId: unitId.value || undefined,
      section: section.value || undefined,
      keyword: submittedKeyword.value || undefined,
      page: page.value,
      size: size.value
    }, { signal: controller.signal })
    if (current === requestId) {
      items.value = response.items
      totalCount.value = response.totalCount
      const lastPage = Math.max(1, Math.ceil(response.totalCount / size.value))
      if (page.value > lastPage) page.value = lastPage
    }
  } catch (cause: unknown) {
    if (current === requestId) error.value = getApiError(cause).message
  } finally {
    if (current === requestId) loading.value = false
  }
}

watch(bookId, (selectedBook) => {
  chosenBook.value = bookOptions.value.find(book => book.id === selectedBook) ?? null
  positionTarget.value = null
  detailWordId.value = null
  unitId.value = ''
  section.value = ''
  page.value = 1
  void loadUnits(selectedBook)
})
watch(unitId, () => { section.value = ''; page.value = 1 })
watch([section, submittedKeyword, size], () => { page.value = 1 })
watch([bookId, unitId, section, submittedKeyword, size, page], () => { void load() })

function search() { submittedKeyword.value = keyword.value.trim(); page.value = 1; void load() }
function resetFilters() { unitId.value = ''; section.value = ''; keyword.value = ''; submittedKeyword.value = ''; page.value = 1; void load() }
function openDetail(row: AdminPhrasePosition, event: Event) {
  detailTrigger.value = event.currentTarget as HTMLButtonElement
  detailWordId.value = row.wordId
}
function refocusDetailTrigger() {
  if (detailTrigger.value?.isConnected) detailTrigger.value.focus()
  else bookPicker.value?.querySelector<HTMLInputElement>('input')?.focus()
}

function editPosition(row: AdminPhrasePosition, action: 'move' | 'remove') {
  positionTarget.value = {
    action, bookId: row.bookId, bookName: bookOptions.value.find(book => book.id === row.bookId)?.bookName ?? '未知教材',
    meaningId: row.meaningId, word: row.word, meaning: row.meaning,
    from: { unitId: row.unitId, section: row.section, entryKind: row.entryKind },
    sourceNumber: row.number, sourceTitle: row.title
  }
}

function positionUnknown() {
  void load()
  detailGeneration.value++
}

function positionChanged() {
  void load()
  detailGeneration.value++
}

onMounted(() => { void loadBooks() })
onBeforeUnmount(() => { requestId += 1; unitRequestId += 1; bookRequestId += 1; controller?.abort(); unitController?.abort(); bookController?.abort() })
</script>

<template>
  <section class="phrase-positions">
    <PageHeader class="phrase-positions__header" title="短语管理" description="每行是一条短语位置；总数与分页均按短语位置计算。">
      <template #actions><RouterLink to="/import/phrase">新增短语</RouterLink></template>
    </PageHeader>
    <section class="phrase-positions__panel">
      <div class="phrase-positions__filters">
        <div ref="bookPicker" class="phrase-positions__book-wrap">
          <el-select v-model="bookId" class="phrase-positions__book" aria-label="选择教材" placeholder="选择教材（可搜索）" clearable filterable remote :remote-method="searchBooks" :loading="booksLoading">
            <el-option v-for="book in bookOptions" :key="book.id" :label="book.bookName + (book.status ? '' : '（停用）')" :value="book.id" />
            <template #empty><p class="phrase-positions__book-empty">{{ booksError ? '教材列表加载失败' : '没有可选择的教材' }}</p></template>
            <template #footer>
              <div class="phrase-positions__book-footer">
                <template v-if="booksError"><span>教材列表加载失败</span><el-button link @click="loadBooks">重试教材列表</el-button></template>
                <template v-else>
                  <span>共 {{ bookTotalCount }} 本 · 第 {{ bookPage }}/{{ bookTotalPage }} 页</span>
                  <el-button link :disabled="bookPage <= 1 || booksLoading" aria-label="上一页教材" @click="turnBookPage(-1)">上一页</el-button>
                  <el-button link :disabled="bookPage >= bookTotalPage || booksLoading" aria-label="下一页教材" @click="turnBookPage(1)">下一页</el-button>
                </template>
              </div>
            </template>
          </el-select>
        </div>
        <el-select v-model="unitId" class="phrase-positions__unit" aria-label="筛选单元" placeholder="全部单元" :disabled="!bookId || unitsLoading || !!unitsError">
          <el-option label="全部单元" value="" />
          <el-option v-for="unit in units" :key="unit.id" :label="`第 ${unit.number} 单元 ${unit.title ?? ''}`" :value="unit.id" />
        </el-select>
        <el-select v-model="section" class="phrase-positions__section" aria-label="筛选分节" :disabled="!bookId">
          <el-option label="全部分节" value="" /><el-option label="Section A" value="A" />
          <el-option label="Section B" value="B" /><el-option label="未分节" value="none" />
        </el-select>
        <el-input v-model="keyword" class="phrase-positions__search" aria-label="搜索短语" placeholder="搜索短语" :disabled="!bookId" clearable @keyup.enter="search" />
        <el-button type="primary" :disabled="!bookId" @click="search">搜索</el-button>
        <el-button :disabled="!bookId" @click="resetFilters">重置筛选</el-button>
      </div>
      <el-alert v-if="booksError" class="phrase-positions__alert" type="error" :title="`教材加载失败：${booksError}`" :closable="false" show-icon><el-button @click="loadBooks">重试教材列表</el-button></el-alert>
      <el-alert v-if="unitsError" class="phrase-positions__alert" type="error" :title="`单元加载失败：${unitsError}`" :closable="false" show-icon><el-button @click="loadUnits(bookId)">重试单元列表</el-button></el-alert>
      <el-empty v-if="!bookId" description="请选择教材查看短语位置。" :image-size="80" />
      <el-alert v-else-if="error" class="phrase-positions__alert" type="error" :title="`加载失败：${error}`" :closable="false" show-icon><el-button @click="load">重试</el-button></el-alert>
      <template v-else>
        <p role="status">{{ loading ? '正在加载短语位置…' : `共 ${totalCount} 条短语位置` }}</p>
        <el-table v-loading="loading" :data="items" stripe border scrollbar-tabindex="0" :row-key="(row: AdminPhrasePosition) => `${row.unitId}:${row.meaningId}:${row.section ?? ''}`">
          <el-table-column label="单元" min-width="130"><template #default="{ row }">第 {{ row.number }} 单元 {{ row.title }}</template></el-table-column>
          <el-table-column label="分节" min-width="100"><template #default="{ row }">{{ row.section ?? '未分节' }}</template></el-table-column>
          <el-table-column prop="word" label="短语" min-width="160" />
          <el-table-column label="英式音标" min-width="130"><template #default="{ row }">{{ row.phoneticUk || '—' }}</template></el-table-column>
          <el-table-column label="美式音标" min-width="130"><template #default="{ row }">{{ row.phoneticUs || '—' }}</template></el-table-column>
          <el-table-column label="词性" min-width="100"><template #default="{ row }">{{ row.partOfSpeech || '—' }}</template></el-table-column>
          <el-table-column prop="meaning" label="释义" min-width="180" />
          <el-table-column label="操作" width="230" fixed="right">
            <template #default="{ row }">
              <el-button link type="primary" @click="openDetail(row, $event)">详情</el-button>
              <el-button link @click="editPosition(row, 'move')">调整位置</el-button>
              <el-button link type="danger" @click="editPosition(row, 'remove')">从本单元移除</el-button>
            </template>
          </el-table-column>
          <template #empty><el-empty v-if="!loading" :image-size="80" :description="unitId || section || submittedKeyword ? '没有匹配的短语位置。' : '该教材暂无短语位置。'" /></template>
        </el-table>
        <div class="phrase-positions__pagination">
          <el-select v-model="size" class="phrase-positions__size" aria-label="每页条数"><el-option v-for="option in [10, 20, 50, 100]" :key="option" :label="`${option} 条/页`" :value="option" /></el-select>
          <el-pagination v-model:current-page="page" :page-size="size" :total="totalCount" layout="total, prev, pager, next, jumper" background />
        </div>
      </template>
    </section>
    <VocabularyDetailDrawer :key="detailGeneration" v-model="detailWordId" @closed="refocusDetailTrigger" @changed="load" />
    <MeaningPositionDialog v-model="positionTarget" @changed="positionChanged" @unknown="positionUnknown" />
  </section>
</template>

<style scoped>
.phrase-positions { min-width: 0; }
.phrase-positions__panel { min-width: 0; padding: var(--lx-space-4); border: 1px solid var(--lx-color-border-light); border-radius: var(--lx-radius-md); background: var(--lx-color-bg-surface); box-shadow: var(--lx-shadow-1); }
.phrase-positions__filters { display: flex; flex-wrap: wrap; gap: var(--lx-space-3); margin-bottom: var(--lx-space-4); }
.phrase-positions__book-wrap { width: min(280px, 100%); }
.phrase-positions__book { width: 100%; }
.phrase-positions__unit, .phrase-positions__section { width: min(200px, 100%); }
.phrase-positions__search { width: min(260px, 100%); }
.phrase-positions__alert { margin-bottom: var(--lx-space-4); }
.phrase-positions__book-empty { margin: 0; padding: var(--lx-space-2); color: var(--lx-color-text-secondary); text-align: center; }
.phrase-positions__book-footer { display: flex; align-items: center; gap: var(--lx-space-2); font-size: var(--lx-font-size-xs); }
.phrase-positions__pagination { display: flex; flex-wrap: wrap; justify-content: flex-end; align-items: center; gap: var(--lx-space-3); margin-top: var(--lx-space-4); }
.phrase-positions__size { width: 120px; }
</style>
