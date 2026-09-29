<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { getBooks, getBookUnits } from '@/services/bookApi'
import { getAdminPhrasePositions } from '@/services/adminVocabularyApi'
import type { AdminPhrasePosition, AdminUnitSectionFilter } from '@/services/adminVocabularyApi'
import type { Book, BookUnit } from '@/types'
import { getApiError } from '@/services/apiError'
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
const size = 20
const items = ref<AdminPhrasePosition[]>([])
const totalCount = ref(0)
const loading = ref(false)
const error = ref('')
const detailWordId = ref<string | null>(null)
const detailGeneration = ref(0)
const positionTarget = ref<PositionTarget | null>(null)
let requestId = 0
let unitRequestId = 0
let controller: AbortController | null = null

async function loadBooks() {
  booksLoading.value = true
  booksError.value = ''
  try {
    const result: Book[] = []
    let current = 1
    while (true) {
      const response = await getBooks({ page: current, size: 100 })
      result.push(...response.items)
      if (current >= response.totalPage) break
      current += 1
    }
    books.value = result
  } catch (cause: unknown) {
    booksError.value = getApiError(cause).message
  } finally {
    booksLoading.value = false
  }
}

async function loadUnits(selectedBook: string) {
  const current = ++unitRequestId
  units.value = []
  unitsError.value = ''
  if (!selectedBook) return
  unitsLoading.value = true
  try {
    const response = await getBookUnits(selectedBook)
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
  totalCount.value = 0
  error.value = ''
  if (!bookId.value) {
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
      size
    }, { signal: controller.signal })
    if (current === requestId) {
      items.value = response.items
      totalCount.value = response.totalCount
      const lastPage = Math.max(1, Math.ceil(response.totalCount / size))
      if (page.value > lastPage) page.value = lastPage
    }
  } catch (cause: unknown) {
    if (current === requestId) error.value = getApiError(cause).message
  } finally {
    if (current === requestId) loading.value = false
  }
}

watch(bookId, (selectedBook) => {
  positionTarget.value = null
  unitId.value = ''
  section.value = ''
  page.value = 1
  void loadUnits(selectedBook)
  void load()
})
watch([unitId, section], () => { page.value = 1; void load() })
watch(page, () => { void load() })

function search() {
  submittedKeyword.value = keyword.value.trim()
  page.value = 1
  void load()
}

function editPosition(row: AdminPhrasePosition, action: 'move' | 'remove') {
  positionTarget.value = {
    action, bookId: row.bookId, bookName: books.value.find(book => book.id === row.bookId)?.bookName ?? '未知教材',
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
onBeforeUnmount(() => { requestId += 1; unitRequestId += 1; controller?.abort() })
</script>

<template>
  <section class="phrase-positions">
    <header class="phrase-positions__header page-header">
      <div><h1>短语管理</h1><p>每行是一条短语位置；总数与分页均按短语位置计算。</p></div>
      <RouterLink to="/import/phrase">新增短语</RouterLink>
    </header>
    <div class="phrase-positions__filters">
      <el-select v-model="bookId" class="phrase-positions__book" aria-label="选择教材" placeholder="选择教材" filterable :loading="booksLoading">
        <el-option v-for="book in books" :key="book.id" :label="book.bookName + (book.status ? '' : '（停用）')" :value="book.id" />
      </el-select>
      <el-select v-model="unitId" class="phrase-positions__unit" aria-label="筛选单元" placeholder="全部单元" :disabled="!bookId || unitsLoading">
        <el-option label="全部单元" value="" />
        <el-option v-for="unit in units" :key="unit.id" :label="`第 ${unit.number} 单元 ${unit.title ?? ''}`" :value="unit.id" />
      </el-select>
      <el-select v-model="section" class="phrase-positions__section" aria-label="筛选分节" :disabled="!bookId">
        <el-option label="全部分节" value="" /><el-option label="Section A" value="A" />
        <el-option label="Section B" value="B" /><el-option label="未分节" value="none" />
      </el-select>
      <el-input v-model="keyword" aria-label="搜索短语" placeholder="搜索短语" :disabled="!bookId" @keyup.enter="search" />
      <el-button :disabled="!bookId" @click="search">搜索</el-button>
    </div>
    <p v-if="booksError" role="alert">教材加载失败：{{ booksError }} <el-button @click="loadBooks">重试</el-button></p>
    <p v-if="unitsError" role="alert">单元加载失败：{{ unitsError }} <el-button @click="loadUnits(bookId)">重试</el-button></p>
    <p v-if="!bookId">请选择教材查看短语位置。</p>
    <p v-else-if="error" role="alert">加载失败：{{ error }} <el-button @click="load">重试</el-button></p>
    <template v-else>
      <p>共 {{ totalCount }} 条短语位置</p>
      <el-table v-loading="loading" :data="items" :row-key="(row: AdminPhrasePosition) => `${row.unitId}:${row.meaningId}:${row.section ?? ''}`">
        <el-table-column label="单元" min-width="130"><template #default="{ row }">第 {{ row.number }} 单元 {{ row.title }}</template></el-table-column>
        <el-table-column label="分节" min-width="100"><template #default="{ row }">{{ row.section ?? '未分节' }}</template></el-table-column>
        <el-table-column prop="word" label="短语" min-width="160" />
        <el-table-column prop="partOfSpeech" label="词性" min-width="100" />
        <el-table-column prop="meaning" label="释义" min-width="180" />
        <el-table-column label="操作" min-width="230">
          <template #default="{ row }">
            <el-button link @click="detailWordId = row.wordId">详情</el-button>
            <el-button link @click="editPosition(row, 'move')">调整位置</el-button>
            <el-button link type="danger" @click="editPosition(row, 'remove')">从本单元移除</el-button>
          </template>
        </el-table-column>
      </el-table>
      <p v-if="!loading && items.length === 0">没有匹配的短语位置。</p>
      <el-pagination v-if="totalCount > size" :current-page="page" :page-size="size" :total="totalCount" layout="prev, pager, next" @current-change="page = $event" />
    </template>
    <VocabularyDetailDrawer :key="detailGeneration" v-model="detailWordId" @changed="load" />
    <MeaningPositionDialog v-model="positionTarget" @changed="positionChanged" @unknown="positionUnknown" />
  </section>
</template>

<style scoped>
.phrase-positions__header { display: flex; justify-content: space-between; align-items: center; gap: 1rem; }
.phrase-positions__filters { display: flex; flex-wrap: wrap; gap: .75rem; margin: 1rem 0; }
.phrase-positions__filters > * { min-width: 170px; }
</style>
