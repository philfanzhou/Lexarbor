<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { searchAdminVocabulary } from '@/services/adminVocabularyApi'
import type { AdminWordSummary } from '@/services/adminVocabularyApi'
import { getBooks } from '@/services/bookApi'
import type { Book } from '@/types'
import { getApiError } from '@/services/apiError'
import PageHeader from '@/components/PageHeader.vue'
import VocabularyDetailDrawer from '@/components/VocabularyDetailDrawer.vue'

/**
 * The whole-library word list. The administration search deduplicates words,
 * includes words held only by disabled books, and includes historical words
 * with no book at all. The book filter reads the paged administration book
 * search (disabled books included) with remote search and paging, so a
 * deployment with many books still offers the later pages; the public
 * enabled-only picker the import pages use is a different component and is
 * not touched here.
 */

const items = ref<AdminWordSummary[]>([])
const totalCount = ref(0)

const keyword = ref('')
const searchedKeyword = ref('')
const bookId = ref('')
const page = ref(1)
const size = ref(20)
const pageSizes = [10, 20, 50, 100]

const loading = ref(false)
const loadError = ref('')

const detailWordId = ref<string | null>(null)
const detailTrigger = ref<HTMLButtonElement>()

let generation = 0
let controller: AbortController | null = null

function load() {
  generation += 1
  const current = generation
  controller?.abort()
  controller = new AbortController()

  loading.value = true
  searchedKeyword.value = keyword.value

  searchAdminVocabulary(
    {
      keyword: keyword.value.trim() || undefined,
      bookId: bookId.value || undefined,
      page: page.value,
      size: size.value
    },
    { signal: controller.signal }
  ).then(
    (data) => {
      if (current !== generation) {
        return
      }

      items.value = data.items
      totalCount.value = data.totalCount
      loading.value = false
      loadError.value = ''
    },
    (error: unknown) => {
      if (current !== generation) {
        return
      }

      loading.value = false
      const message = getApiError(error).message
      ElMessage.error(message)
      loadError.value = message
    }
  )
}

function handleSearch() {
  page.value = 1
  load()
}

function handleFilterChange(value: string | number | boolean | undefined) {
  if (typeof value !== 'string' || value === '') {
    chosenBook.value = null
  } else {
    pickChosenBook(String(value))
  }

  page.value = 1
  load()
}

function handleSizeChange(newSize: number) {
  size.value = newSize
  page.value = 1
  load()
}

function handlePageChange(newPage: number) {
  page.value = newPage
  load()
}

function openDetail(row: AdminWordSummary, event: Event) {
  detailTrigger.value = event.currentTarget as HTMLButtonElement
  detailWordId.value = row.id
}

function refocusDetailTrigger() {
  detailTrigger.value?.focus()
}

// The book filter's own paged source, independent of the word list request.
const filterBooks = ref<Book[]>([])
const filterKeyword = ref('')
const filterPage = ref(1)
const filterTotalPage = ref(1)
const filterTotalCount = ref(0)
const filterLoading = ref(false)
const filterError = ref(false)
const filterPageSize = 20

// The chosen book stays selectable even when paging or searching the filter
// away from it, so the closed select never degrades to showing a raw ID.
const chosenBook = ref<Book | null>(null)
const filterOptions = computed(() =>
  chosenBook.value && !filterBooks.value.some((book) => book.id === chosenBook.value?.id)
    ? [chosenBook.value, ...filterBooks.value]
    : filterBooks.value
)

function pickChosenBook(id: string) {
  chosenBook.value = filterBooks.value.find((book) => book.id === id) ?? null
}

let filterGeneration = 0
let filterController: AbortController | null = null

function loadFilterBooks() {
  filterGeneration += 1
  const current = filterGeneration
  filterController?.abort()
  filterController = new AbortController()

  filterLoading.value = true
  filterError.value = false

  getBooks(
    { keyword: filterKeyword.value.trim() || undefined, page: filterPage.value, size: filterPageSize },
    { signal: filterController.signal }
  ).then(
    (data) => {
      if (current !== filterGeneration) {
        return
      }

      filterBooks.value = data.items
      filterTotalCount.value = data.totalCount
      filterTotalPage.value = data.totalPage
      filterLoading.value = false
    },
    () => {
      if (current !== filterGeneration) {
        return
      }

      filterLoading.value = false
      filterError.value = true
    }
  )
}

function remoteFilterBooks(query: string) {
  filterKeyword.value = query
  filterPage.value = 1
  loadFilterBooks()
}

function filterPageBackward() {
  if (filterPage.value > 1) {
    filterPage.value -= 1
    loadFilterBooks()
  }
}

function filterPageForward() {
  if (filterPage.value < filterTotalPage.value) {
    filterPage.value += 1
    loadFilterBooks()
  }
}

function filterBookLabel(book: Book) {
  return `${book.bookName}${book.status ? '' : '（停用）'}`
}

onMounted(() => {
  load()
  loadFilterBooks()
})

onBeforeUnmount(() => {
  generation += 1
  controller?.abort()
  filterGeneration += 1
  filterController?.abort()
})
</script>

<template>
  <div class="vocabulary-view">
    <PageHeader
      title="单词管理"
      description="全库去重单词，包含仅属于停用教材与无教材归属的单词"
    />

    <section class="words-panel">
      <div class="toolbar">
        <el-input
          v-model="keyword"
          class="toolbar__search"
          aria-label="搜索单词"
          placeholder="搜索单词"
          clearable
          @keyup.enter="handleSearch"
        />
        <el-select
          v-model="bookId"
          class="toolbar__book"
          aria-label="筛选教材"
          placeholder="筛选教材（可搜索）"
          clearable
          filterable
          remote
          :remote-method="remoteFilterBooks"
          :loading="filterLoading"
          @change="handleFilterChange"
        >
          <el-option
            v-for="book in filterOptions"
            :key="book.id"
            :label="filterBookLabel(book)"
            :value="book.id"
          />
          <template #empty>
            <p class="book-filter__empty">
              {{ filterError ? '教材列表加载失败' : '没有可选择的教材' }}
            </p>
          </template>
          <template #footer>
            <div v-if="filterError" class="book-filter__footer">
              <span class="book-filter__status">教材列表加载失败</span>
              <el-button link type="primary" size="small" @click="loadFilterBooks">重试</el-button>
            </div>
            <div v-else class="book-filter__footer">
              <span class="book-filter__status">共 {{ filterTotalCount }} 本 · 第 {{ filterPage }}/{{ filterTotalPage }} 页</span>
              <span class="book-filter__pager">
                <el-button
                  link
                  size="small"
                  :disabled="filterPage <= 1"
                  aria-label="上一页教材"
                  @click="filterPageBackward"
                >
                  上一页
                </el-button>
                <el-button
                  link
                  size="small"
                  :disabled="filterPage >= filterTotalPage"
                  aria-label="下一页教材"
                  @click="filterPageForward"
                >
                  下一页
                </el-button>
              </span>
            </div>
          </template>
        </el-select>
        <el-button type="primary" @click="handleSearch">搜索</el-button>
      </div>

      <el-alert
        v-if="loadError"
        class="vocabulary-error"
        type="error"
        :title="loadError"
        :closable="false"
        show-icon
      >
        <el-button class="vocabulary-error__retry" size="small" @click="load">重试</el-button>
      </el-alert>

      <el-table v-loading="loading" :data="items" stripe border scrollbar-tabindex="0">
        <el-table-column prop="word" label="单词" min-width="140" />
        <el-table-column label="英式音标" min-width="130">
          <template #default="{ row }">{{ row.phoneticUk ?? '—' }}</template>
        </el-table-column>
        <el-table-column label="美式音标" min-width="130">
          <template #default="{ row }">{{ row.phoneticUs ?? '—' }}</template>
        </el-table-column>
        <el-table-column label="教材归属" min-width="260">
          <template #default="{ row }">
            <span v-if="row.books.length" class="vocabulary-books">
              <el-tag
                v-for="book in row.books"
                :key="book.id"
                :type="book.status ? 'success' : 'info'"
                size="small"
              >
                {{ book.bookName }}{{ book.status ? '' : '（停用）' }}
              </el-tag>
            </span>
            <el-tag v-else type="info" size="small">无教材归属</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="90" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openDetail(row, $event)">详情</el-button>
          </template>
        </el-table-column>
        <template #empty>
          <el-empty
            v-if="!loadError"
            :image-size="80"
            :description="searchedKeyword || bookId ? '没有匹配的单词' : '词库中没有单词'"
          />
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
    </section>

    <VocabularyDetailDrawer v-model="detailWordId" @closed="refocusDetailTrigger" />
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

.toolbar {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lx-space-3);
  margin-bottom: var(--lx-space-4);
}
.toolbar__search {
  width: min(260px, 100%);
}
.toolbar__book {
  width: min(280px, 100%);
}

.vocabulary-error {
  margin-bottom: var(--lx-space-4);
}
.vocabulary-error__retry {
  margin-top: var(--lx-space-2);
}

.vocabulary-books {
  display: inline-flex;
  flex-wrap: wrap;
  gap: var(--lx-space-1);
}

.book-filter__empty {
  margin: 0;
  padding: var(--lx-space-2) 0;
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-xs);
  text-align: center;
}

.book-filter__footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--lx-space-3);
  padding: var(--lx-space-1) var(--lx-space-3) var(--lx-space-2);
}
.book-filter__status {
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-xs);
}
.book-filter__pager {
  display: inline-flex;
  gap: var(--lx-space-1);
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
