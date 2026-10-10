<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  getAdminVocabularyWord,
  updateAdminMeaning,
  updateAdminVocabularyWord
} from '@/services/adminVocabularyApi'
import type {
  AdminBookRef,
  AdminCleanupResult,
  AdminCleanupSelection,
  AdminMeaning,
  AdminMeaningEditPayload,
  AdminWordDetail,
  AdminWordEditPayload
} from '@/services/adminVocabularyApi'
import { getApiError } from '@/services/apiError'
import VocabularyCleanupDialog from '@/components/VocabularyCleanupDialog.vue'
import MeaningPositionDialog from '@/components/MeaningPositionDialog.vue'
import type { PositionTarget } from '@/components/MeaningPositionDialog.vue'

/**
 * The word detail shared by both administration lists. One target generation:
 * switching the word (or closing) aborts the in-flight read, and a late success
 * or failure can never paint the word that replaced it; unmounting ends the
 * generation the same way. Authentication failures keep the shared
 * interceptor's clearing and redirect.
 *
 * Two independent edit groups live here. The shared spelling and phonetics
 * replace through one API and every meaning replaces through its own; each
 * group has its own loading, error, and success state, one save in flight at a
 * time, and neither promises an atomic combined save. A save keeps the draft on
 * a definite refusal (400 shows the reason, 409 offers an explicit reload, 404
 * reports the target is gone), while an answer that never arrives re-reads the
 * current detail instead of replaying the write. Unsubmitted drafts belong to
 * the target that opened them and are dropped when the target changes.
 *
 * Each meaning also carries a delete entry that goes through the shared
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
const positionTarget = ref<PositionTarget | null>(null)

/** The shared-fields draft; initialized from the server values when editing starts. */
const sharedForm = reactive({ word: '', phoneticUk: '', phoneticUs: '' })
const sharedEditing = ref(false)
const sharedSaving = ref(false)
const sharedError = ref('')
/** Whether the error offers an explicit reload of the server state (409/404). */
const sharedErrorReload = ref(false)

/** One meaning's independent draft and save state, keyed by meaning id. */
interface MeaningEdit {
  draft: { partOfSpeech: string; meaning: string; example: string }
  saving: boolean
  error: string
  errorReload: boolean
}

const meaningEdits = ref<Record<string, MeaningEdit | undefined>>({})

let generation = 0
let controller: AbortController | null = null

function blankToNull(value: string): string | null {
  const trimmed = value.trim()
  return trimmed ? trimmed : null
}

/** Drafts belong to one target: a new read starts from the server values again. */
function resetEditState() {
  positionTarget.value = null
  sharedEditing.value = false
  sharedSaving.value = false
  sharedError.value = ''
  sharedErrorReload.value = false
  sharedForm.word = ''
  sharedForm.phoneticUk = ''
  sharedForm.phoneticUs = ''
  meaningEdits.value = {}
}

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
  resetEditState()

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

function editPosition(meaning: AdminMeaning, unit: NonNullable<AdminMeaning['units']>[number], bookName: string, action: 'move' | 'remove') {
  positionTarget.value = {
    action,
    bookId: meaning.bookId,
    bookName,
    meaningId: meaning.id,
    word: detail.value?.word ?? '',
    meaning: meaning.meaning,
    partOfSpeech: meaning.partOfSpeech ?? null,
    sourceNumber: unit.number,
    sourceTitle: unit.title,
    from: {
      unitId: unit.unitId,
      section: unit.section === 'A' || unit.section === 'B' ? unit.section : null,
      entryKind: unit.entryKind === 'word' || unit.entryKind === 'phrase' ? unit.entryKind : null
    }
  }
}

function handlePositionChanged() {
  emit('changed')
  load()
}

function handlePositionUnknown() {
  emit('changed')
  load()
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
    resetEditState()
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
  const byBookId = new Map<
    string,
    { book: AdminBookRef | null; meanings: Array<{ meaning: AdminMeaning; edit: MeaningEdit | null }> }
  >()
  const knownBooks = new Map((detail.value?.books ?? []).map((book) => [book.id, book]))
  for (const meaning of detail.value?.meanings ?? []) {
    const book = knownBooks.get(meaning.bookId) ?? null
    const group = byBookId.get(meaning.bookId) ?? { book, meanings: [] }
    group.meanings.push({ meaning, edit: meaningEdits.value[meaning.id] ?? null })
    byBookId.set(meaning.bookId, group)
  }

  return [...byBookId.values()]
})

function displayValue(value?: string | null) {
  const trimmed = value?.trim()
  return trimmed ? trimmed : '—'
}

function startSharedEdit() {
  if (!detail.value) {
    return
  }

  sharedForm.word = detail.value.word
  sharedForm.phoneticUk = detail.value.phoneticUk ?? ''
  sharedForm.phoneticUs = detail.value.phoneticUs ?? ''
  sharedError.value = ''
  sharedErrorReload.value = false
  sharedEditing.value = true
}

function cancelSharedEdit(event: Event) {
  const trigger = event.currentTarget as HTMLButtonElement | null
  sharedEditing.value = false
  sharedError.value = ''
  sharedErrorReload.value = false
  void nextTick(() => trigger?.focus())
}

/** The confirmation names every referencing book, disabled ones included. */
function sharedConfirmMessage() {
  const books = detail.value?.books ?? []
  if (!books.length) {
    return '该单词当前没有被任何教材引用；保存仍会更新这个单词的拼写与音标。'
  }

  const names = books
    .map((book) => (book.status ? book.bookName : `${book.bookName}（停用）`))
    .join('、')
  return `拼写与音标在各教材间共用，保存后会影响引用该单词的全部教材（含停用）：${names}。`
}

/**
 * Maps a refused save. A definite refusal keeps the draft with a reason; 409
 * and 404 additionally offer an explicit reload. An answer that never arrived
 * (no response) has an unknown outcome: the current detail is re-read and null
 * is returned because the re-read already reset the form.
 */
/** Mirrors the server: phonetics are refused only when every position of the spelling is a phrase. */
function usedOnlyAsPhrase(word: AdminWordDetail) {
  const units = word.meanings.flatMap(meaning => meaning.units ?? [])
  return units.length > 0 && units.every(unit => unit.entryKind === 'phrase')
}

/** An unchanged value is kept so that existing data stays editable. */
function isNewValue(requested: string | null, current: string | null | undefined) {
  return requested !== null && requested !== (current ?? null)
}

function describeSaveFailure(
  error: unknown,
  goneMessage: string
): { message: string; reload: boolean } | null {
  const apiError = getApiError(error)
  if (apiError.status === undefined) {
    ElMessage.warning('保存结果未知，已重新读取当前数据；请核对后再决定是否重新保存')
    load()
    return null
  }

  if (apiError.status === 404) {
    return { message: `保存失败：${goneMessage}`, reload: true }
  }
  if (apiError.status === 409) {
    return {
      message: `保存失败：${apiError.message} 数据可能已被他人修改，可重新加载最新内容后再试。`,
      reload: true
    }
  }

  return { message: `保存失败：${apiError.message}`, reload: false }
}

async function saveShared() {
  const target = detail.value
  if (!target || sharedSaving.value) {
    return
  }

  sharedError.value = ''
  sharedErrorReload.value = false
  const word = sharedForm.word.trim()
  if (!word) {
    sharedError.value = '请输入单词拼写'
    return
  }

  if (usedOnlyAsPhrase(target) &&
    (isNewValue(blankToNull(sharedForm.phoneticUk), target.phoneticUk) ||
      isNewValue(blankToNull(sharedForm.phoneticUs), target.phoneticUs))) {
    sharedError.value = '这个拼写只用作短语，短语不填写音标'
    return
  }

  try {
    await ElMessageBox.confirm(sharedConfirmMessage(), '确认修改拼写与音标', {
      type: 'warning',
      confirmButtonText: '保存',
      cancelButtonText: '取消'
    })
  } catch {
    // Cancelled before any request: the draft stays exactly as it was.
    return
  }

  const payload: AdminWordEditPayload = {
    word,
    phoneticUk: blankToNull(sharedForm.phoneticUk),
    phoneticUs: blankToNull(sharedForm.phoneticUs)
  }

  const current = generation
  sharedSaving.value = true
  try {
    await updateAdminVocabularyWord(target.id, payload)
    if (current !== generation) {
      return
    }

    ElMessage.success('拼写与音标已保存')
    emit('changed')
    load()
  } catch (error: unknown) {
    if (current !== generation) {
      return
    }

    const failure = describeSaveFailure(error, '该单词不存在或已被删除')
    if (failure) {
      sharedError.value = failure.message
      sharedErrorReload.value = failure.reload
    }
  } finally {
    sharedSaving.value = false
  }
}

function startMeaningEdit(meaning: AdminMeaning) {
  meaningEdits.value = {
    ...meaningEdits.value,
    [meaning.id]: {
      draft: {
        partOfSpeech: meaning.partOfSpeech ?? '',
        meaning: meaning.meaning,
        example: meaning.example ?? ''
      },
      saving: false,
      error: '',
      errorReload: false
    }
  }
}

function cancelMeaningEdit(meaningId: string, event: Event) {
  const trigger = event.currentTarget as HTMLButtonElement | null
  const next = { ...meaningEdits.value }
  delete next[meaningId]
  meaningEdits.value = next
  void nextTick(() => trigger?.focus())
}

async function saveMeaning(meaning: AdminMeaning) {
  const edit = meaningEdits.value[meaning.id]
  const target = detail.value
  if (!edit || !target || edit.saving) {
    return
  }

  edit.error = ''
  edit.errorReload = false
  const definition = edit.draft.meaning.trim()
  if (!definition) {
    edit.error = '请输入释义'
    return
  }

  const payload: AdminMeaningEditPayload = {
    partOfSpeech: blankToNull(edit.draft.partOfSpeech),
    meaning: definition,
    example: blankToNull(edit.draft.example)
  }
  if (meaning.units?.some(unit => unit.entryKind === 'phrase') &&
    isNewValue(payload.partOfSpeech?.toLowerCase() ?? null, meaning.partOfSpeech)) {
    edit.error = '这条释义有短语位置，短语不填写词性'
    return
  }

  const current = generation
  edit.saving = true
  try {
    await updateAdminMeaning(meaning.bookId, meaning.vocabularyId, meaning.id, payload)
    if (current !== generation) {
      return
    }

    ElMessage.success('释义已保存')
    emit('changed')
    load()
  } catch (error: unknown) {
    if (current !== generation) {
      return
    }

    const failure = describeSaveFailure(error, '该释义、单词或教材不存在或已被删除')
    if (failure) {
      edit.error = failure.message
      edit.errorReload = failure.reload
    }
  } finally {
    edit.saving = false
  }
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
        <section class="word-detail__summary" aria-label="拼写与音标">
          <template v-if="!sharedEditing">
            <h2 class="word-detail__word">{{ detail.word }}</h2>
            <p class="word-detail__phonetics">
              <span>英 {{ displayValue(detail.phoneticUk) }}</span>
              <span>美 {{ displayValue(detail.phoneticUs) }}</span>
            </p>
          </template>

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

          <el-button
            v-if="!sharedEditing"
            link
            type="primary"
            class="word-detail__edit-toggle"
            @click="startSharedEdit"
          >
            编辑拼写与音标
          </el-button>

          <div v-else class="word-detail__edit" role="group" aria-label="编辑拼写与音标">
            <el-alert
              v-if="sharedError"
              class="word-detail__edit-error"
              type="error"
              :title="sharedError"
              :closable="false"
              show-icon
            >
              <el-button v-if="sharedErrorReload" size="small" @click="load">重新加载</el-button>
            </el-alert>

            <el-form class="word-detail__form" label-position="top" @submit.prevent="saveShared">
              <el-form-item label="单词拼写（各教材共用）">
                <el-input v-model="sharedForm.word" :disabled="sharedSaving" />
              </el-form-item>
              <el-form-item label="英式音标（各教材共用，留空保存为空）">
                <el-input v-model="sharedForm.phoneticUk" :disabled="sharedSaving" />
              </el-form-item>
              <el-form-item label="美式音标（各教材共用，留空保存为空）">
                <el-input v-model="sharedForm.phoneticUs" :disabled="sharedSaving" />
              </el-form-item>
              <p class="word-detail__edit-hint">
                拼写与音标在各教材间共用，保存影响以上全部教材；留空的可选音标会明确清空，保存前需确认。
              </p>
              <div class="word-detail__edit-actions">
                <el-button :disabled="sharedSaving" @click="cancelSharedEdit">取消</el-button>
                <el-button type="primary" :loading="sharedSaving" @click="saveShared">
                  保存拼写与音标
                </el-button>
              </div>
            </el-form>
          </div>
        </section>

        <p class="word-detail__hint">
          拼写与音标在各教材间共用；以下释义按教材分组，每条释义仅属于对应教材，可单独编辑。词条/短语类别属于每个单元位置。
        </p>

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
            <li
              v-for="{ meaning, edit } in group.meanings"
              :key="meaning.id"
              class="word-detail__meaning"
            >
              <div v-if="!edit" class="word-detail__meaning-read">
                <p class="word-detail__meaning-line">
                  <el-tag v-if="meaning.partOfSpeech" size="small" type="primary">
                    {{ meaning.partOfSpeech }}
                  </el-tag>
                  <span>{{ meaning.meaning }}</span>
                  <span v-for="unit in meaning.units ?? []" :key="`${unit.unitId}:${unit.section ?? ''}:${unit.entryKind ?? ''}`" class="word-detail__position">
                    <el-tag class="word-detail__unit-tag" size="small" type="info">
                      单元 {{ unit.number }}{{ unit.title ? ` · ${unit.title}` : '' }} · {{ unit.section ? `Section ${unit.section}` : '未分节' }} · {{ unit.entryKind === 'word' ? '词条' : unit.entryKind === 'phrase' ? '短语' : '未分类' }}
                    </el-tag>
                    <el-button link :aria-label="`调整位置：单元 ${unit.number} ${unit.section ?? '未分节'} ${unit.entryKind ?? '未分类'}`" @click="editPosition(meaning, unit, group.book?.bookName ?? '未知教材', 'move')">调整位置</el-button>
                    <el-button link type="danger" :aria-label="`从本单元移除：单元 ${unit.number} ${unit.section ?? '未分节'} ${unit.entryKind ?? '未分类'}`" @click="editPosition(meaning, unit, group.book?.bookName ?? '未知教材', 'remove')">从本单元移除</el-button>
                  </span>
                </p>
                <p v-if="meaning.example" class="word-detail__example">{{ meaning.example }}</p>
                <div class="word-detail__meaning-actions">
                  <el-button link type="primary" @click="startMeaningEdit(meaning)">编辑</el-button>
                  <el-button
                    link
                    type="danger"
                    :aria-label="`删除整条释义及其全部位置：教材 ${group.book?.bookName ?? '未知'}`"
                    @click="removeMeaning(meaning)"
                  >
                    删除整条释义及其全部位置
                  </el-button>
                </div>
              </div>

              <div
                v-else
                class="word-detail__meaning-edit"
                role="group"
                :aria-label="`编辑教材 ${group.book?.bookName ?? '未知'} 的这条释义`"
              >
                <el-alert
                  v-if="edit.error"
                  class="word-detail__edit-error"
                  type="error"
                  :title="edit.error"
                  :closable="false"
                  show-icon
                >
                  <el-button v-if="edit.errorReload" size="small" @click="load">重新加载</el-button>
                </el-alert>

                <el-form class="word-detail__form" label-position="top" @submit.prevent="saveMeaning(meaning)">
                  <el-form-item label="词性（仅本教材，可清空）">
                    <el-input v-model="edit.draft.partOfSpeech" :disabled="edit.saving" />
                  </el-form-item>
                  <el-form-item label="释义（仅本教材）">
                    <el-input
                      v-model="edit.draft.meaning"
                      type="textarea"
                      :rows="2"
                      :disabled="edit.saving"
                    />
                  </el-form-item>
                  <el-form-item label="例句（仅本教材，可清空）">
                    <el-input
                      v-model="edit.draft.example"
                      type="textarea"
                      :rows="2"
                      :disabled="edit.saving"
                    />
                  </el-form-item>
                  <div class="word-detail__edit-actions">
                    <el-button :disabled="edit.saving" @click="cancelMeaningEdit(meaning.id, $event)">
                      取消
                    </el-button>
                    <el-button type="primary" :loading="edit.saving" @click="saveMeaning(meaning)">
                      保存本条释义
                    </el-button>
                  </div>
                </el-form>
              </div>
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
  <MeaningPositionDialog v-model="positionTarget" @changed="handlePositionChanged" @unknown="handlePositionUnknown" />
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
.word-detail__edit-toggle {
  margin-top: var(--lx-space-2);
}

.word-detail__edit {
  margin-top: var(--lx-space-3);
}
.word-detail__edit-error {
  margin-bottom: var(--lx-space-3);
}
.word-detail__form :deep(.el-form-item) {
  margin-bottom: var(--lx-space-3);
}
.word-detail__edit-hint {
  margin: 0 0 var(--lx-space-3);
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-xs);
}
.word-detail__edit-actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--lx-space-2);
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
.word-detail__meaning-read {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--lx-space-1);
}
.word-detail__meaning-read .el-button {
  margin-top: var(--lx-space-1);
  align-self: flex-start;
}
.word-detail__meaning-actions {
  display: flex;
  gap: var(--lx-space-3);
  margin-top: var(--lx-space-1);
  align-self: flex-start;
}
.word-detail__meaning-actions .el-button {
  margin-top: 0;
  align-self: auto;
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
.word-detail__unit-tag {
  flex-shrink: 0;
}
.word-detail__position { display: inline-flex; align-items: center; flex-wrap: wrap; gap: var(--lx-space-1); }
.word-detail__meaning-edit {
  width: 100%;
}
</style>
