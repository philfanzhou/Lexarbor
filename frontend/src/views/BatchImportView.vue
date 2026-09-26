<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { getActiveBooks } from '@/services/bookApi'
import { importVocabularyBatch } from '@/services/vocabularyApi'
import type { VocabularyBatchImportPayload, VocabularyBatchImportResult } from '@/services/vocabularyApi'
import { decodeUtf8, formatForFileName, parseVocabularyInput } from '@/services/vocabularyInput'
import type { VocabularyFileFormat, VocabularyInputFormat } from '@/services/vocabularyInput'
import { readVocabularyWorkbook } from '@/services/vocabularyXlsx'
import type { VocabularyWorkbook } from '@/services/vocabularyXlsx'
import { getApiError } from '@/services/apiError'
import PageHeader from '@/components/PageHeader.vue'
import type { Book } from '@/types'

// Both limits are the server's constants (ADR-005). Checking them here only
// spares a request the server would refuse; the server still checks them.
const MAX_ENTRIES = 500
const MAX_BYTES = 1_048_576
const PAGE_SIZE = 100

const formatLabels: Record<VocabularyInputFormat, string> = { tsv: 'TSV', csv: 'CSV', json: 'JSON' }
const placeholders: Record<VocabularyInputFormat, string> = {
  tsv: '每行一条，制表符分隔：单词、英式音标、美式音标、词性、释义，可选第 6 列例句；空行和 # 开头的行忽略',
  csv: '第一行为表头，逗号分隔，例如：word,phonetic_uk,phonetic_us,part_of_speech,meaning,example',
  json: '[{"word":"apple","meaning":"苹果"}]'
}
/** The help panel's text for each format; what every format shares is in the template. */
const formatHints: Record<VocabularyFileFormat, { title: string; rules: string[]; example: string }> = {
  tsv: {
    title: 'TSV：制表符分隔',
    rules: [
      '没有表头，每行一条，列之间用制表符（Tab）分隔',
      '列的顺序固定：单词、英式音标、美式音标、词性、释义，可选的第 6 列例句',
      '空行和 # 开头的行忽略'
    ],
    example: 'apple\t/ˈæp.əl/\t/ˈæp.əl/\tn.\t苹果\tI eat an apple.'
  },
  csv: {
    title: 'CSV：逗号分隔',
    rules: [
      '第一行为表头，可用的列名：word、phonetic_uk、phonetic_us、part_of_speech、meaning、example',
      '表头不区分大小写，顺序不限；word 和 meaning 必填',
      '只支持逗号分隔'
    ],
    example: 'word,phonetic_uk,phonetic_us,part_of_speech,meaning,example\napple,/ˈæp.əl/,/ˈæp.əl/,n.,苹果,I eat an apple.'
  },
  json: {
    title: 'JSON：对象数组',
    rules: [
      '顶层是一个数组，每项是一个对象',
      '字段名与 API 相同：word、phoneticUk、phoneticUs、partOfSpeech、meaning、example；word 和 meaning 必填'
    ],
    example: '[\n  { "word": "apple", "phoneticUk": "/ˈæp.əl/", "meaning": "苹果" },\n  { "word": "banana", "meaning": "香蕉" }\n]'
  },
  xlsx: {
    title: 'Excel：.xlsx 工作簿',
    rules: [
      '只读取第一个工作表',
      '第一条非空行是表头，列名和规则与 CSV 相同'
    ],
    example: 'word | phonetic_uk | phonetic_us | part_of_speech | meaning | example\napple | /ˈæp.əl/ | /ˈæp.əl/ | n. | 苹果 | I eat an apple.'
  }
}
// A JSON row is numbered by its item in the array, not by a line.
const positionLabels: Record<VocabularyInputFormat, string> = { tsv: '行号', csv: '行号', json: '序号' }

/** A chosen `.xlsx` file. Its workbook is undefined while the worker is still reading it. */
interface ExcelFile {
  name: string
  workbook?: VocabularyWorkbook
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
/** Set while an `.xlsx` file is loaded; the text area is unused and disabled then. */
const excelFile = ref<ExcelFile>()
/** Stops the worker reading the current `.xlsx` file, if it is still reading. */
let excelReading: AbortController | undefined

// Excel is chosen only by choosing a file, and left only by removing it.
const selectedFormat = computed<VocabularyFileFormat>({
  get: () => (excelFile.value ? 'xlsx' : format.value),
  set: (value) => {
    if (value !== 'xlsx') {
      format.value = value
    }
  }
})
const readingExcel = computed(() => excelFile.value !== undefined && excelFile.value.workbook === undefined)
const formatHelp = computed(() => formatHints[selectedFormat.value])
const sheetNotice = computed(() => excelFile.value?.workbook?.notice)

const parsed = computed(() =>
  excelFile.value
    ? excelFile.value.workbook ?? { rows: [] }
    : parseVocabularyInput(format.value, text.value)
)
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

// Display only: what the preview section says while it has no row to show.
const showPreviewEmpty = computed(() => !parseError.value && rows.value.length === 0 && !readingExcel.value)
const previewEmptyText = computed(() =>
  text.value || excelFile.value ? '没有可预览的数据行' : '粘贴数据或选择文件后，这里会显示预览'
)

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
  if (readingExcel.value) {
    reasons.push('正在读取 Excel 文件')
    return reasons
  }
  // A file-level error leaves no rows, so it is the only reason worth reading.
  if (parseError.value) {
    reasons.push(parseError.value)
    return reasons
  }
  if (rows.value.length === 0) {
    reasons.push(excelFile.value
      ? '没有可导入的数据行，请检查第一个工作表'
      : `没有可导入的数据行，请粘贴 ${formatLabels[format.value]} 文本或选择文件`)
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

// A server verdict belongs to the exact text, format, file, and book it was given for.
watch([format, text, excelFile, bookId], () => {
  serverErrors.value = new Map()
  serverSummary.value = ''
  currentPage.value = 1
  // The last result stays up after the text is cleared on success, and goes
  // once a new batch is being prepared.
  if (text.value || excelFile.value) {
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

function stopReadingExcel() {
  excelReading?.abort()
  excelReading = undefined
}

/** Leaves Excel mode for an empty TSV text area, the page's starting state. */
function removeFile() {
  stopReadingExcel()
  excelFile.value = undefined
  format.value = 'tsv'
  text.value = ''
}

async function openWorkbook(name: string, buffer: ArrayBuffer) {
  stopReadingExcel()
  const reading = new AbortController()
  excelReading = reading
  format.value = 'tsv'
  text.value = ''
  excelFile.value = { name }

  const workbook = await readVocabularyWorkbook(buffer, reading.signal)
  // Removed, or replaced by another file, while it was being read.
  if (reading.signal.aborted) {
    return
  }
  excelReading = undefined
  excelFile.value = { name, workbook }
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
    ElMessage.error('不支持的文件类型，请选择 .tsv、.txt 或 .csv 文件，也可以选择 .json 文件或 .xlsx 文件')
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

  if (fileFormat === 'xlsx') {
    await openWorkbook(file.name, buffer)
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

  stopReadingExcel()
  excelFile.value = undefined
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
    stopReadingExcel()
    excelFile.value = undefined
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
onBeforeUnmount(stopReadingExcel)
</script>

<template>
  <div class="batch-import-view">
    <PageHeader title="批量导入" description="从文本或本地文件一次向一本教材导入多个单词" />

    <section class="batch-section" aria-labelledby="batch-step-book">
      <h2 id="batch-step-book" class="batch-section__title">1. 选择教材</h2>
      <el-form label-position="top">
        <el-form-item label="教材">
          <el-select
            v-model="bookId"
            class="batch-book-select"
            placeholder="请选择教材"
            :disabled="submitting"
          >
            <el-option
              v-for="book in books"
              :key="book.id"
              :label="book.bookName"
              :value="book.id"
            />
          </el-select>
        </el-form-item>
      </el-form>
    </section>

    <section class="batch-section" aria-labelledby="batch-step-input">
      <h2 id="batch-step-input" class="batch-section__title">2. 输入数据</h2>
      <div class="batch-entry">
        <div class="batch-input">
          <el-radio-group
            v-model="selectedFormat"
            class="batch-format"
            aria-label="数据格式"
            :disabled="submitting || !!excelFile"
          >
            <el-radio-button value="tsv">TSV</el-radio-button>
            <el-radio-button value="csv">CSV</el-radio-button>
            <el-radio-button value="json">JSON</el-radio-button>
            <el-radio-button value="xlsx" disabled>Excel</el-radio-button>
          </el-radio-group>
          <el-input
            v-model="text"
            type="textarea"
            aria-label="导入数据"
            :rows="10"
            :disabled="submitting || !!excelFile"
            :placeholder="excelFile ? excelFile.name : placeholders[format]"
          />
          <div class="batch-input__actions">
            <el-button :disabled="submitting" @click="chooseFile">选择文件</el-button>
            <el-button v-if="excelFile" class="batch-remove-file" :disabled="submitting" @click="removeFile">
              移除文件
            </el-button>
            <input
              ref="fileInput"
              class="batch-file-input"
              type="file"
              accept=".tsv,.txt,.csv,.json,.xlsx,text/tab-separated-values,text/plain,text/csv,application/json,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
              @change="handleFileChange"
            >
          </div>
        </div>

        <div class="batch-help">
          <h3 class="batch-help__title">{{ formatHelp.title }}</h3>
          <ul class="batch-help__list">
            <li v-for="rule in formatHelp.rules" :key="rule">{{ rule }}</li>
          </ul>
          <p class="batch-help__label">示例</p>
          <pre class="batch-help__example"><code>{{ formatHelp.example }}</code></pre>

          <h3 class="batch-help__title">所有格式</h3>
          <ul class="batch-help__list">
            <li>可以选择 .tsv、.txt、.csv、.json 文件（UTF-8 编码）或 .xlsx 文件，单个文件不超过 1 MiB</li>
            <li>单批不超过 {{ MAX_ENTRIES }} 条</li>
            <li>整批在一个事务中写入：任何一条失败，整批都不写入</li>
            <li>文件只在浏览器中解析，不会上传</li>
          </ul>
        </div>
      </div>
    </section>

    <section class="batch-section" aria-labelledby="batch-step-preview">
      <h2 id="batch-step-preview" class="batch-section__title">3. 预览与校验</h2>

      <el-alert
        v-if="sheetNotice"
        class="batch-sheet-notice"
        type="info"
        :closable="false"
        show-icon
        :title="sheetNotice"
      />

      <el-alert
        v-if="parseError"
        class="batch-parse-error"
        type="error"
        :closable="false"
        show-icon
        :title="parseError"
      />

      <el-alert
        v-if="serverSummary"
        class="batch-server-summary"
        type="error"
        :closable="false"
        show-icon
        :title="serverSummary"
      />

      <template v-if="!parseError && rows.length > 0">
        <div class="batch-toolbar">
          <span class="batch-summary">
            数据行 {{ rows.length }} 条，有效 {{ rows.length - invalidCount }} 条，<span class="batch-summary__invalid" :class="{ 'is-nonzero': invalidCount > 0 }">无效 {{ invalidCount }} 条</span>
          </span>
          <el-switch
            v-model="onlyInvalid"
            class="batch-only-invalid"
            aria-label="只看无效行"
            active-text="只看无效行"
          />
        </div>

        <el-table
          :data="pagedRows"
          :row-class-name="rowClassName"
          border
          size="small"
          class="batch-preview"
          scrollbar-tabindex="0"
        >
          <el-table-column prop="position" :label="positionLabels[format]" width="70" />
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

      <el-empty
        v-if="showPreviewEmpty"
        class="batch-preview-empty"
        :image-size="80"
        :description="previewEmptyText"
      />
    </section>

    <section class="batch-section" aria-labelledby="batch-step-submit">
      <h2 id="batch-step-submit" class="batch-section__title">4. 提交与结果</h2>

      <el-alert
        v-if="blockers.length > 0"
        class="batch-blocked"
        type="warning"
        title="暂时无法提交"
        :closable="false"
        show-icon
      >
        <ul class="batch-blockers">
          <li v-for="reason in blockers" :key="reason">{{ reason }}</li>
        </ul>
      </el-alert>

      <div class="batch-submit">
        <el-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="handleSubmit">
          提交导入
        </el-button>
      </div>

      <el-alert
        v-if="result"
        class="batch-result"
        type="success"
        :closable="false"
        show-icon
        :title="`导入完成：总计 ${result.total} 条，新增 ${result.created} 条，复用 ${result.reused} 条`"
      />
    </section>
  </div>
</template>

<style scoped>
.batch-import-view {
  container-type: inline-size;
}

.batch-section {
  min-width: 0;
  margin-bottom: var(--lx-space-4);
  padding: var(--lx-space-5);
  border: 1px solid var(--lx-color-border-light);
  border-radius: var(--lx-radius-md);
  background: var(--lx-color-bg-surface);
  box-shadow: var(--lx-shadow-1);
}
.batch-section__title {
  margin: 0 0 var(--lx-space-4);
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-md);
  font-weight: 600;
}
.batch-section :deep(.el-form-item) {
  margin-bottom: 0;
}

.batch-book-select {
  width: min(400px, 100%);
}

.batch-entry {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: var(--lx-space-5);
  align-items: start;
}
.batch-input {
  display: flex;
  flex-direction: column;
  gap: var(--lx-space-3);
  min-width: 0;
}
.batch-format {
  align-self: flex-start;
}
.batch-input__actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--lx-space-3);
}
.batch-input__actions .el-button + .el-button {
  margin-left: 0;
}
.batch-file-input {
  display: none;
}

.batch-help {
  min-width: 0;
  padding: var(--lx-space-4);
  border: 1px solid var(--lx-color-border-light);
  border-radius: var(--lx-radius-md);
  background: var(--lx-color-bg-surface);
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-sm);
  line-height: 1.6;
}
.batch-help__title {
  margin: 0 0 var(--lx-space-2);
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-sm);
  font-weight: 600;
}
.batch-help__title ~ .batch-help__title {
  margin-top: var(--lx-space-4);
}
.batch-help__list {
  margin: 0;
  padding-left: var(--lx-space-5);
}
.batch-help__label {
  margin: var(--lx-space-3) 0 var(--lx-space-1);
}
.batch-help__example {
  margin: 0;
  padding: var(--lx-space-2) var(--lx-space-3);
  border-radius: var(--lx-radius-sm);
  background: var(--lx-color-bg-page);
  color: var(--lx-color-text-regular);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, 'Liberation Mono', monospace;
  font-size: var(--lx-font-size-xs);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  tab-size: 4;
}

.batch-sheet-notice,
.batch-parse-error,
.batch-server-summary {
  margin-bottom: var(--lx-space-3);
}
.batch-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: var(--lx-space-3);
  margin-bottom: var(--lx-space-2);
}
.batch-summary {
  color: var(--lx-color-text-regular);
  font-size: var(--lx-font-size-sm);
}
.batch-summary__invalid {
  color: var(--lx-color-danger);
}
.batch-summary__invalid.is-nonzero {
  font-weight: 600;
}
.batch-pagination {
  justify-content: flex-end;
  margin-top: var(--lx-space-3);
}
.batch-status--invalid {
  color: var(--lx-color-danger);
}
.batch-status--valid {
  color: var(--lx-color-success);
}
:deep(.batch-row--invalid) {
  background: var(--el-color-danger-light-9);
}

.batch-blocked {
  margin-bottom: var(--lx-space-3);
}
.batch-blockers {
  margin: var(--lx-space-1) 0 0;
  padding-left: var(--lx-space-5);
  color: var(--lx-color-warning);
  font-size: var(--lx-font-size-sm);
}
.batch-result {
  margin-top: var(--lx-space-3);
}

@media (max-width: 1023px) {
  .batch-section {
    padding: var(--lx-space-4);
  }
}

@container (min-width: 1000px) {
  .batch-entry {
    grid-template-columns: minmax(0, 1fr) minmax(280px, 360px);
  }
}
</style>
