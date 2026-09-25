<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { getActiveBooks } from '@/services/bookApi'
import { importVocabularyBatch } from '@/services/vocabularyApi'
import type { VocabularyBatchImportPayload, VocabularyBatchImportResult } from '@/services/vocabularyApi'
import { decodeUtf8, formatForFileName, parseVocabularyInput } from '@/services/vocabularyInput'
import type { VocabularyInputFormat } from '@/services/vocabularyInput'
import { getApiError } from '@/services/apiError'
import type { Book } from '@/types'

// Both limits are the server's constants (ADR-005). Checking them here only
// spares a request the server would refuse; the server still checks them.
const MAX_ENTRIES = 500
const MAX_BYTES = 1_048_576
const PAGE_SIZE = 100

const formatLabels: Record<VocabularyInputFormat, string> = { tsv: 'TSV', csv: 'CSV' }
const placeholders: Record<VocabularyInputFormat, string> = {
  tsv: '每行一条，制表符分隔：单词、英式音标、美式音标、词性、释义，可选第 6 列例句；空行和 # 开头的行忽略',
  csv: '第一行为表头，逗号分隔，例如：word,phonetic_uk,phonetic_us,part_of_speech,meaning,example'
}
const formatHints: Record<VocabularyInputFormat, string> = {
  tsv: '无表头，列顺序固定',
  csv: '只支持逗号分隔；表头不区分大小写、顺序不限，必须包含 word 和 meaning'
}

const books = ref<Book[]>([])
const bookId = ref('')
const format = ref<VocabularyInputFormat>('tsv')
const text = ref('')
const submitting = ref(false)
const onlyInvalid = ref(false)
const currentPage = ref(1)
const result = ref<VocabularyBatchImportResult>()
/** Server reasons keyed by row position; valid only for the current format, text, and book. */
const serverErrors = ref(new Map<number, string>())
const serverSummary = ref('')
const fileInput = ref<HTMLInputElement>()

const parsed = computed(() => parseVocabularyInput(format.value, text.value))
const rows = computed(() => parsed.value.rows)
const parseError = computed(() => parsed.value.error)

const previewRows = computed(() =>
  rows.value.map((row) => {
    const serverError = serverErrors.value.get(row.position)
    const reason = row.error ?? (serverError === undefined ? undefined : `服务端：${serverError}`)
    return { ...row, reason }
  })
)

const invalidCount = computed(() => previewRows.value.filter((row) => row.reason).length)

const visibleRows = computed(() =>
  onlyInvalid.value ? previewRows.value.filter((row) => row.reason) : previewRows.value
)

const pagedRows = computed(() => {
  const start = (currentPage.value - 1) * PAGE_SIZE
  return visibleRows.value.slice(start, start + PAGE_SIZE)
})

/**
 * What would be sent now. Built only from a fully valid preview, so the entries
 * are the preview's rows in the same order and `entries[i]` is `rows[i]`.
 */
const payload = computed<VocabularyBatchImportPayload | undefined>(() => {
  if (rows.value.some((row) => !row.entry)) {
    return undefined
  }

  return { bookId: bookId.value, entries: rows.value.map((row) => row.entry!) }
})

const payloadBytes = computed(() =>
  payload.value ? new Blob([JSON.stringify(payload.value)]).size : 0
)

const blockers = computed(() => {
  const reasons: string[] = []
  if (!bookId.value) {
    reasons.push('请选择教材')
  }
  // A file-level error leaves no rows, so it is the only reason worth reading.
  if (parseError.value) {
    reasons.push(parseError.value)
    return reasons
  }
  if (rows.value.length === 0) {
    reasons.push(`没有可导入的数据行，请粘贴 ${formatLabels[format.value]} 文本或选择文件`)
  }
  if (invalidCount.value > 0) {
    reasons.push(`存在 ${invalidCount.value} 行无效数据，请修正后再提交`)
  }
  if (rows.value.length > MAX_ENTRIES) {
    reasons.push(
      `数据行共 ${rows.value.length} 条，超过单批 ${MAX_ENTRIES} 条上限，请把超出的 ${rows.value.length - MAX_ENTRIES} 条拆到下一批`
    )
  }
  if (payloadBytes.value > MAX_BYTES) {
    reasons.push('请求体超过 1 MiB，请拆成更小的批次')
  }
  return reasons
})

const canSubmit = computed(() => blockers.value.length === 0 && !submitting.value)

// A server verdict belongs to the exact text, format, and book it was given for.
watch([format, text, bookId], () => {
  serverErrors.value = new Map()
  serverSummary.value = ''
  currentPage.value = 1
  // The last result stays up after the text is cleared on success, and goes
  // once a new batch is being prepared.
  if (text.value) {
    result.value = undefined
  }
})

watch(onlyInvalid, () => {
  currentPage.value = 1
})

async function loadBooks() {
  try {
    const data = await getActiveBooks()
    books.value = data.books
  } catch (error: unknown) {
    ElMessage.error(getApiError(error).message)
  }
}

function chooseFile() {
  fileInput.value?.click()
}

/** Counts file reads, so that only the file chosen last fills the text area. */
let fileReads = 0

async function handleFileChange(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  // Cleared so that choosing the same file again still fires a change.
  input.value = ''
  if (!file) {
    return
  }

  // Extension, then size, then content (ADR-006): a file is refused before it
  // is read whenever it can be.
  const fileFormat = formatForFileName(file.name)
  if (!fileFormat) {
    ElMessage.error('不支持的文件类型，请选择 .tsv、.txt 或 .csv 文件')
    return
  }

  // A guard for the preview, which would otherwise try to render a file far
  // larger than any batch the server accepts.
  if (file.size > MAX_BYTES) {
    ElMessage.error('文件超过 1 MiB，请拆分后分批导入')
    return
  }

  const read = ++fileReads
  let buffer: ArrayBuffer
  try {
    buffer = await file.arrayBuffer()
  } catch {
    if (read === fileReads) {
      ElMessage.error('文件读取失败')
    }
    return
  }
  if (read !== fileReads) {
    return
  }

  let content: string
  try {
    content = decodeUtf8(buffer)
  } catch {
    // Refused rather than shown with replacement characters, which would
    // otherwise be a valid preview and be stored as they are.
    ElMessage.error('文件不是 UTF-8 编码，请另存为 UTF-8（Excel：CSV UTF-8（逗号分隔））后重试')
    return
  }

  format.value = fileFormat
  text.value = content
}

function showServerEntryErrors(errors: { index: number; message: string }[]): boolean {
  const mapped = new Map<number, string>()
  for (const { index, message } of errors) {
    const row = rows.value[index]
    if (!row) {
      return false
    }
    mapped.set(row.position, message)
  }

  serverErrors.value = mapped
  serverSummary.value = `${mapped.size} 行未通过服务端校验，整批未写入`
  onlyInvalid.value = true
  currentPage.value = 1
  return true
}

async function handleSubmit() {
  if (!canSubmit.value || !payload.value) {
    return
  }

  submitting.value = true
  result.value = undefined
  try {
    result.value = await importVocabularyBatch(payload.value)
    ElMessage.success('导入成功')
    // Cleared so the same batch is not sent twice by accident. Sending it twice
    // would be harmless, but the second result would read as a new import.
    text.value = ''
    onlyInvalid.value = false
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === 401 || apiError.status === 403) {
      // The API client has already redirected.
      return
    }

    if (apiError.status === 400 && apiError.errors?.length && showServerEntryErrors(apiError.errors)) {
      return
    }

    if (apiError.status === undefined) {
      ElMessage.error('网络错误，无法确认是否已写入；重新提交同一批是安全的')
    } else if (apiError.status === 413) {
      ElMessage.error('请求体超过 1 MiB，请拆分为更小的批次后重试')
    } else if (apiError.status === 404) {
      ElMessage.error('所选教材不存在，请刷新后重新选择')
    } else if (apiError.status === 409) {
      ElMessage.error('该单词或词义与现有数据冲突')
    } else if (apiError.status === 422) {
      ElMessage.error('所选教材已停用，无法导入新词义')
    } else if (apiError.status === 503) {
      ElMessage.error('服务繁忙，整批未写入，请稍后重试')
    } else {
      ElMessage.error(apiError.message)
    }
  } finally {
    submitting.value = false
  }
}

function rowClassName({ row }: { row: { reason?: string } }) {
  return row.reason ? 'batch-row--invalid' : ''
}

onMounted(loadBooks)
</script>

<template>
  <div class="batch-import-view">
    <el-card shadow="never">
      <template #header>
        <span class="card-title">批量导入</span>
      </template>

      <el-form label-width="80px">
        <el-form-item label="教材">
          <el-select
            v-model="bookId"
            class="batch-book-select"
            placeholder="请选择教材"
            :disabled="submitting"
            style="width: 100%; max-width: 400px"
          >
            <el-option
              v-for="book in books"
              :key="book.id"
              :label="book.bookName"
              :value="book.id"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="数据">
          <div class="batch-input">
            <div class="batch-input__format">
              <el-radio-group v-model="format" class="batch-format" :disabled="submitting">
                <el-radio-button value="tsv">TSV</el-radio-button>
                <el-radio-button value="csv">CSV</el-radio-button>
              </el-radio-group>
              <span class="batch-hint batch-format-hint">{{ formatHints[format] }}</span>
            </div>
            <el-input
              v-model="text"
              type="textarea"
              :rows="8"
              :disabled="submitting"
              :placeholder="placeholders[format]"
            />
            <div class="batch-input__actions">
              <el-button :disabled="submitting" @click="chooseFile">选择文件</el-button>
              <span class="batch-hint">读取本地 .tsv / .txt / .csv 文件（UTF-8，不超过 1 MiB）；文件只在浏览器中解析，不会上传</span>
              <input
                ref="fileInput"
                class="batch-file-input"
                type="file"
                accept=".tsv,.txt,.csv,text/tab-separated-values,text/plain,text/csv"
                @change="handleFileChange"
              >
            </div>
          </div>
        </el-form-item>
      </el-form>

      <el-alert
        v-if="result"
        class="batch-result"
        type="success"
        :closable="false"
        show-icon
        :title="`导入完成：总计 ${result.total} 条，新增 ${result.created} 条，复用 ${result.reused} 条`"
      />

      <el-alert
        v-if="serverSummary"
        class="batch-server-summary"
        type="error"
        :closable="false"
        show-icon
        :title="serverSummary"
      />

      <el-alert
        v-if="parseError"
        class="batch-parse-error"
        type="error"
        :closable="false"
        show-icon
        :title="parseError"
      />

      <template v-else-if="rows.length > 0">
        <div class="batch-toolbar">
          <span class="batch-summary">
            数据行 {{ rows.length }} 条，有效 {{ rows.length - invalidCount }} 条，无效 {{ invalidCount }} 条
          </span>
          <el-switch v-model="onlyInvalid" class="batch-only-invalid" active-text="只看无效行" />
        </div>

        <el-table :data="pagedRows" :row-class-name="rowClassName" border size="small" class="batch-preview">
          <el-table-column prop="position" label="行号" width="70" />
          <el-table-column label="单词" min-width="110">
            <template #default="{ row }">{{ row.columns[0] }}</template>
          </el-table-column>
          <el-table-column label="英式音标" min-width="100">
            <template #default="{ row }">{{ row.columns[1] }}</template>
          </el-table-column>
          <el-table-column label="美式音标" min-width="100">
            <template #default="{ row }">{{ row.columns[2] }}</template>
          </el-table-column>
          <el-table-column label="词性" width="70">
            <template #default="{ row }">{{ row.columns[3] }}</template>
          </el-table-column>
          <el-table-column label="释义" min-width="120">
            <template #default="{ row }">{{ row.columns[4] }}</template>
          </el-table-column>
          <el-table-column label="例句" min-width="160">
            <template #default="{ row }">{{ row.columns[5] }}</template>
          </el-table-column>
          <el-table-column label="状态" min-width="180">
            <template #default="{ row }">
              <span v-if="row.reason" class="batch-status batch-status--invalid">{{ row.reason }}</span>
              <span v-else class="batch-status batch-status--valid">有效</span>
            </template>
          </el-table-column>
        </el-table>

        <el-pagination
          v-model:current-page="currentPage"
          class="batch-pagination"
          layout="total, prev, pager, next"
          :page-size="PAGE_SIZE"
          :total="visibleRows.length"
          hide-on-single-page
        />
      </template>

      <ul v-if="blockers.length > 0" class="batch-blockers">
        <li v-for="reason in blockers" :key="reason">{{ reason }}</li>
      </ul>

      <div class="batch-submit">
        <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="handleSubmit">
          提交导入
        </el-button>
      </div>
    </el-card>
  </div>
</template>

<style scoped>
.batch-import-view { padding: 16px; }
.card-title { font-weight: 600; font-size: 16px; }
.batch-input { width: 100%; }
.batch-input__format {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 8px;
}
.batch-input__actions {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 8px;
}
.batch-hint { color: #909399; font-size: 12px; }
.batch-file-input { display: none; }
.batch-result,
.batch-server-summary,
.batch-parse-error { margin-bottom: 12px; }
.batch-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
}
.batch-summary { color: #606266; font-size: 14px; }
.batch-pagination { margin-top: 12px; justify-content: flex-end; }
.batch-status--invalid { color: #f56c6c; }
.batch-status--valid { color: #67c23a; }
.batch-blockers {
  margin: 12px 0 0;
  padding-left: 20px;
  color: #e6a23c;
  font-size: 13px;
}
.batch-submit { margin-top: 12px; }
:deep(.batch-row--invalid) { background: #fef0f0; }
</style>
