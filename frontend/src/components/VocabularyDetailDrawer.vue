<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import {
  getAdminVocabularyWord
} from '@/services/adminVocabularyApi'
import type {
  AdminBookRef,
  AdminCleanupResult,
  AdminCleanupSelection,
  AdminMeaning,
  AdminWordDetail
} from '@/services/adminVocabularyApi'
import { getApiError } from '@/services/apiError'
import VocabularyCleanupDialog from '@/components/VocabularyCleanupDialog.vue'

/**
 * The word detail shared by both administration lists. One target generation:
 * switching the word (or closing) aborts the in-flight request, and a late
 * success or failure can never paint the word that replaced it; unmounting
 * ends the generation the same way. Authentication failures keep the shared
 * interceptor's clearing and redirect.
 *
 * Each meaning carries a delete entry that goes through the shared
 * preview-and-confirm cleanup flow, using the meaning's own book, word, and
 * meaning identifiers. A committed deletion re-reads the detail — only the
 * server's result decides what happened — and when the word lost its last
 * meaning the re-read's 404 closes the drawer instead of painting a missing
 * word. A commit whose answer never arrived re-queries rather than replaying.
 */

const wordId = defineModel<string | null>({ required: true })

const emit = defineEmits<{ closed: []; changed: [] }>()

const drawerOpen = computed({
  get: () => wordId.value !== null,
  set: (open: boolean) => {
    if (!open) {
      wordId.value = null
    }
  }
})

const detail = ref<AdminWordDetail | null>(null)
const loading = ref(false)
const loadError = ref('')
const notFound = ref(false)

/** The shared preview-and-confirm flow for one meaning's deletion. */
const cleanupOpen = ref(false)
const cleanupSelection = ref<AdminCleanupSelection | null>(null)
const cleanupBookId = ref<string | null>(null)
/** After our own cleanup, a 404 re-read means the word itself was deleted. */
const closeOnMissing = ref(false)

let generation = 0
let controller: AbortController | null = null

function load() {
  const target = wordId.value
  if (!target) {
    return
  }

  generation += 1
  const current = generation
  controller?.abort()
  controller = new AbortController()

  loading.value = true
  loadError.value = ''
  notFound.value = false
  detail.value = null

  getAdminVocabularyWord(target, { signal: controller.signal }).then(
    (data) => {
      if (current !== generation) {
        return
      }

      detail.value = data
      loading.value = false
    },
    (error: unknown) => {
      if (current !== generation) {
        return
      }

      loading.value = false
      const apiError = getApiError(error)
      if (apiError.status === 404) {
        if (closeOnMissing.value) {
          // The last meaning took the word with it; the drawer closes rather
          // than reporting a word the administrator just watched disappear.
          closeOnMissing.value = false
          wordId.value = null
          return
        }

        notFound.value = true
      } else {
        loadError.value = apiError.message
      }
    }
  )
}

function removeMeaning(meaning: AdminMeaning) {
  cleanupBookId.value = meaning.bookId
  cleanupSelection.value = {
    action: 'removeMeaning',
    wordId: meaning.vocabularyId,
    meaningId: meaning.id
  }
  cleanupOpen.value = true
}

function handleCleanupCommitted(result: AdminCleanupResult) {
  ElMessage.success(
    `已删除释义 ${result.deletedMeaningCount} 条${result.deletedWordCount ? `，该单词已失去全部教材引用，一并删除 ${result.deletedWordCount} 个单词` : '，其他教材中的释义保留'}`
  )
  closeOnMissing.value = result.deletedWordCount > 0
  emit('changed')
  load()
}

function handleCleanupUnknown() {
  // The commit's answer never arrived; re-read what the server now holds.
  load()
}

watch(wordId, () => {
  if (wordId.value === null) {
    // Closing ends the target too: nothing may land in a closed drawer.
    generation += 1
    controller?.abort()
    return
  }

  load()
})

onBeforeUnmount(() => {
  generation += 1
  controller?.abort()
})

/** Meanings grouped per book, in the stable order the endpoint returns. */
const groupedMeanings = computed(() => {
  const byBookId = new Map<string, { book: AdminBookRef | null; meanings: AdminMeaning[] }>()
  const knownBooks = new Map((detail.value?.books ?? []).map((book) => [book.id, book]))
  for (const meaning of detail.value?.meanings ?? []) {
    const book = knownBooks.get(meaning.bookId) ?? null
    const group = byBookId.get(meaning.bookId) ?? { book, meanings: [] }
    group.meanings.push(meaning)
    byBookId.set(meaning.bookId, group)
  }

  return [...byBookId.values()]
})

function displayValue(value?: string | null) {
  const trimmed = value?.trim()
  return trimmed ? trimmed : '—'
}
</script>

<template>
  <el-drawer
    v-model="drawerOpen"
    class="word-detail-drawer"
    title="单词详情"
    size="min(480px, 100%)"
    @closed="emit('closed')"
  >
    <div v-loading="loading" class="word-detail" aria-live="polite">
      <el-alert
        v-if="loadError"
        class="word-detail__error"
        type="error"
        :title="loadError"
        :closable="false"
        show-icon
      >
        <el-button size="small" @click="load">重试</el-button>
      </el-alert>

      <el-empty
        v-else-if="notFound"
        :image-size="80"
        description="该单词不存在或已被删除"
      />

      <template v-else-if="detail">
        <section class="word-detail__summary" aria-label="共享字段">
          <h2 class="word-detail__word">{{ detail.word }}</h2>
          <p class="word-detail__phonetics">
            <span>英 {{ displayValue(detail.phoneticUk) }}</span>
            <span>美 {{ displayValue(detail.phoneticUs) }}</span>
          </p>
          <p class="word-detail__books">
            <template v-if="detail.books.length">
              <el-tag
                v-for="book in detail.books"
                :key="book.id"
                :type="book.status ? 'success' : 'info'"
                size="small"
              >
                {{ book.bookName }}{{ book.status ? '' : '（停用）' }}
              </el-tag>
            </template>
            <el-tag v-else type="info" size="small">无教材归属</el-tag>
          </p>
        </section>

        <p class="word-detail__hint">拼写与音标为全库共享字段；以下释义按教材分组。</p>

        <section
          v-for="group in groupedMeanings"
          :key="group.book?.id ?? 'unknown'"
          class="word-detail__group"
          :aria-label="`教材 ${group.book?.bookName ?? '未知'} 的释义`"
        >
          <h3 class="word-detail__group-title">
            {{ group.book?.bookName ?? '未知教材' }}
            <el-tag
              v-if="group.book"
              :type="group.book.status ? 'success' : 'info'"
              size="small"
            >
              {{ group.book.status ? '启用' : '停用' }}
            </el-tag>
          </h3>
          <ul class="word-detail__meanings">
            <li v-for="meaning in group.meanings" :key="meaning.id" class="word-detail__meaning">
              <p class="word-detail__meaning-line">
                <el-tag v-if="meaning.partOfSpeech" size="small" type="primary">
                  {{ meaning.partOfSpeech }}
                </el-tag>
                <span>{{ meaning.meaning }}</span>
              </p>
              <p v-if="meaning.example" class="word-detail__example">{{ meaning.example }}</p>
              <el-button
                link
                type="danger"
                class="word-detail__meaning-remove"
                :aria-label="`删除教材 ${group.book?.bookName ?? '未知'} 的这条释义`"
                @click="removeMeaning(meaning)"
              >
                删除
              </el-button>
            </li>
          </ul>
        </section>

        <el-empty
          v-if="!groupedMeanings.length"
          :image-size="80"
          description="该单词暂无释义记录"
        />
      </template>
    </div>
  </el-drawer>

  <VocabularyCleanupDialog
    v-model="cleanupOpen"
    :book-id="cleanupBookId"
    :selection="cleanupSelection"
    @committed="handleCleanupCommitted"
    @unknown="handleCleanupUnknown"
  />
</template>

<style scoped>
.word-detail {
  min-height: 120px;
}

.word-detail__error {
  margin-bottom: var(--lx-space-4);
}

.word-detail__summary {
  padding-bottom: var(--lx-space-4);
  border-bottom: 1px solid var(--lx-color-border-light);
}
.word-detail__word {
  margin: 0;
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-xl);
  font-weight: 600;
  word-break: break-word;
}
.word-detail__phonetics {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lx-space-3);
  margin: var(--lx-space-2) 0 0;
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-sm);
}
.word-detail__books {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lx-space-2);
  margin: var(--lx-space-3) 0 0;
}

.word-detail__hint {
  margin: var(--lx-space-3) 0 0;
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-xs);
}

.word-detail__group + .word-detail__group {
  margin-top: var(--lx-space-4);
}
.word-detail__group-title {
  display: flex;
  align-items: center;
  gap: var(--lx-space-2);
  margin: var(--lx-space-4) 0 var(--lx-space-2);
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-md);
  font-weight: 600;
}
.word-detail__meanings {
  margin: 0;
  padding: 0;
  list-style: none;
}
.word-detail__meaning + .word-detail__meaning {
  margin-top: var(--lx-space-3);
  padding-top: var(--lx-space-3);
  border-top: 1px solid var(--lx-color-border-light);
}
.word-detail__meaning-line {
  display: flex;
  align-items: baseline;
  gap: var(--lx-space-2);
  margin: 0;
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-sm);
  word-break: break-word;
}
.word-detail__example {
  margin: var(--lx-space-1) 0 0;
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-xs);
  word-break: break-word;
}
.word-detail__meaning-remove {
  margin-top: var(--lx-space-1);
  height: auto;
  padding: 0;
}
</style>
