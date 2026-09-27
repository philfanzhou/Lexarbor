<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { getAdminBookContent } from '@/services/adminVocabularyApi'
import type { AdminWordDetail } from '@/services/adminVocabularyApi'
import type { Book } from '@/types'
import { getApiError } from '@/services/apiError'
import PageHeader from '@/components/PageHeader.vue'
import VocabularyDetailDrawer from '@/components/VocabularyDetailDrawer.vue'

/**
 * One book's word list, reached from 教材管理's 查看单词. Keyword, page, and
 * size run on the server; `wordCount`/`meaningCount` count the whole book and
 * stay put while a keyword narrows the page. The route parameter identifies
 * the current target: switching books (or leaving the page) aborts the
 * in-flight request and invalidates its generation, so a late answer cannot
 * paint the book that replaced it. A disabled book is maintained the same way
 * as an enabled one.
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

const loading = ref(false)
const loadError = ref('')
const notFound = ref(false)

const detailWordId = ref<string | null>(null)
const detailTrigger = ref<HTMLButtonElement>()

let generation = 0
let controller: AbortController | null = null

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

  getAdminBookContent(
    bookId.value,
    {
      keyword: keyword.value.trim() || undefined,
      page: page.value,
      size: size.value
    },
    { signal: controller.signal }
  ).then(
    (data) => {
      if (current !== generation) {
        return
      }

      book.value = data.book
      wordCount.value = data.wordCount
      meaningCount.value = data.meaningCount
      items.value = data.items
      totalCount.value = data.totalCount
      loading.value = false
      loadError.value = ''
      notFound.value = false
    },
    (error: unknown) => {
      if (current !== generation) {
        return
      }

      loading.value = false
      const apiError = getApiError(error)
      if (apiError.status === 404) {
        notFound.value = true
      } else {
        ElMessage.error(apiError.message)
        loadError.value = apiError.message
      }
    }
  )
}

watch(bookId, () => {
  keyword.value = ''
  page.value = 1
  book.value = null
  wordCount.value = 0
  meaningCount.value = 0
  items.value = []
  totalCount.value = 0
  detailWordId.value = null
  load()
})

onMounted(() => load())

onBeforeUnmount(() => {
  generation += 1
  controller?.abort()
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

function handlePageChange(newPage: number) {
  page.value = newPage
  load()
}

function backToBooks() {
  void router.push({ name: 'books' })
}

function openDetail(row: AdminWordDetail, event: Event) {
  detailTrigger.value = event.currentTarget as HTMLButtonElement
  detailWordId.value = row.id
}

function refocusDetailTrigger() {
  detailTrigger.value?.focus()
}

const emptyDescription = computed(() =>
  searchedKeyword.value ? '没有匹配的单词' : '该教材暂无单词'
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

      <template v-else>
        <p v-if="book" class="book-words__meta">
          <el-tag :type="book.status ? 'success' : 'info'" size="small">
            {{ book.status ? '启用' : '停用' }}
          </el-tag>
          <span>去重单词 {{ wordCount }}</span>
          <span>释义 {{ meaningCount }}</span>
        </p>

        <div class="toolbar">
          <el-input
            v-model="keyword"
            class="toolbar__search"
            aria-label="搜索单词"
            placeholder="搜索单词"
            clearable
            @keyup.enter="handleSearch"
          />
          <el-button type="primary" @click="handleSearch">搜索</el-button>
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

        <el-table v-loading="loading" :data="items" stripe border scrollbar-tabindex="0">
          <el-table-column prop="word" label="单词" min-width="140" />
          <el-table-column label="英式音标" min-width="130">
            <template #default="{ row }">{{ row.phoneticUk ?? '—' }}</template>
          </el-table-column>
          <el-table-column label="美式音标" min-width="130">
            <template #default="{ row }">{{ row.phoneticUs ?? '—' }}</template>
          </el-table-column>
          <el-table-column label="本教材释义" min-width="260">
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
          <el-table-column label="操作" width="90" fixed="right">
            <template #default="{ row }">
              <el-button link type="primary" @click="openDetail(row, $event)">详情</el-button>
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
  gap: var(--lx-space-3);
  margin-bottom: var(--lx-space-4);
}
.toolbar__search {
  width: min(320px, 100%);
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
