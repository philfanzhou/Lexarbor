<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { addBookUnit, deleteBookUnit, getBookUnits, updateBookUnit } from '@/services/bookApi'
import type { BookUnit } from '@/types'
import { getApiError } from '@/services/apiError'

/**
 * The book row's 单元 entry: one dialog that lists a book's units with their
 * assignment counts and maintains them. Creating a unit is the
 * administrator's explicit act here — import never creates or renames one.
 * A refused write keeps the entered values: 400 shows the server's reason,
 * 409 explains the conflict and offers a re-read, 404 reports the target
 * gone. A write whose answer never arrived is an unknown outcome: the list
 * is re-read and the write is never replayed on its own.
 */

const props = defineProps<{
  book: { id: string; bookName: string } | null
}>()

const open = defineModel<boolean>({ required: true })

const units = ref<BookUnit[]>([])
const loading = ref(false)
/** 404 from the list read: the book was deleted elsewhere. */
const bookGone = ref(false)
const loadError = ref('')
const loadRetryable = ref(false)

const editing = ref<BookUnit | null>(null)
const formNumber = ref<number | null>(null)
const formTitle = ref('')
const submitting = ref(false)
const formError = ref('')
/** Set when a write was refused in a way a fresh list read explains. */
const formStale = ref(false)

watch(
  () => [open.value, props.book?.id] as const,
  () => {
    if (open.value && props.book) {
      resetForm()
      void reload()
    }
  }
)

function resetForm() {
  editing.value = null
  formNumber.value = null
  formTitle.value = ''
  formError.value = ''
  formStale.value = false
}

async function reload() {
  if (!props.book || loading.value) {
    return
  }

  loading.value = true
  bookGone.value = false
  loadError.value = ''
  loadRetryable.value = false
  try {
    const data = await getBookUnits(props.book.id)
    units.value = data.units
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === 404) {
      bookGone.value = true
      loadError.value = '目标教材不存在或已被删除；关闭本弹窗即可返回教材列表。'
    } else if (apiError.status === undefined) {
      loadRetryable.value = true
      loadError.value = '网络异常，未能取得单元列表；该列表只读不写入，可重试。'
    } else if (apiError.status === 503) {
      loadRetryable.value = true
      loadError.value = '服务忙，暂时无法取得单元列表，请稍后重试。'
    } else {
      loadError.value = `加载失败：${apiError.message}`
    }
  } finally {
    loading.value = false
  }
}

function startEdit(unit: BookUnit) {
  editing.value = unit
  formNumber.value = unit.number
  formTitle.value = unit.title ?? ''
  formError.value = ''
  formStale.value = false
}

async function submit() {
  if (!props.book || submitting.value) {
    return
  }

  if (formNumber.value === null || !Number.isInteger(formNumber.value) || formNumber.value < 1) {
    formError.value = '单元编号必须是正整数。'
    return
  }

  // The write is a replace, not a merge: the title is sent every time, and
  // an empty one is the explicit null.
  const trimmedTitle = formTitle.value.trim()
  const payload = { number: formNumber.value, title: trimmedTitle === '' ? null : trimmedTitle }

  submitting.value = true
  formError.value = ''
  formStale.value = false
  try {
    if (editing.value) {
      await updateBookUnit(props.book.id, editing.value.id, payload)
      ElMessage.success('单元已更新')
    } else {
      await addBookUnit(props.book.id, payload)
      ElMessage.success('单元已添加')
    }
    resetForm()
    await reload()
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === undefined) {
      // The write was sent and never answered: the outcome is unknown. Ask
      // for a re-read; this dialog never resends by itself.
      formError.value = '提交结果未知，请对照当前列表确认后再决定，不要盲目重复提交。'
      formStale.value = true
    } else if (apiError.status === 400) {
      formError.value = `未提交成功：${apiError.message}`
    } else if (apiError.status === 409) {
      formStale.value = true
      formError.value = `编号冲突：${apiError.message} 请对照当前列表后重试。`
    } else if (apiError.status === 404) {
      formStale.value = true
      formError.value = editing.value
        ? '目标单元或教材已不存在，请刷新列表。'
        : '目标教材已不存在，请刷新列表。'
    } else {
      formError.value = `提交失败：${apiError.message}`
    }
  } finally {
    submitting.value = false
  }
}

async function remove(unit: BookUnit) {
  if (!props.book) {
    return
  }

  const label = unit.title ? `单元 ${unit.number}「${unit.title}」` : `单元 ${unit.number}`
  try {
    await ElMessageBox.confirm(
      `删除${label}会移除该单元及其 ${unit.meaningCount} 条词义归属；词义、共享词条和其他单元的归属会保留。`,
      '确认删除单元',
      { type: 'warning' }
    )
  } catch {
    // Cancelled before any request; nothing changed.
    return
  }

  try {
    await deleteBookUnit(props.book.id, unit.id)
    ElMessage.success('单元已删除')
    if (editing.value?.id === unit.id) {
      resetForm()
    }
    await reload()
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === undefined) {
      ElMessage.warning('删除结果未知，请查看当前列表后再决定，不要盲目重复提交')
      await reload()
    } else if (apiError.status === 404) {
      ElMessage.error('目标单元或教材已不存在，请刷新列表')
      await reload()
    } else if (apiError.status === 409) {
      ElMessage.error(`未能删除：${apiError.message} 请刷新列表后重试`)
      await reload()
    } else {
      ElMessage.error(apiError.message)
    }
  }
}
</script>

<template>
  <el-dialog
    v-model="open"
    class="unit-dialog"
    :title="props.book ? `单元管理：${props.book.bookName}` : '单元管理'"
    width="min(640px, calc(100vw - 32px))"
    :close-on-click-modal="false"
  >
    <div v-loading="loading" class="unit-dialog__body">
      <el-alert
        v-if="loadError"
        class="unit-dialog__notice"
        :type="bookGone ? 'warning' : 'error'"
        :title="loadError"
        :closable="false"
        show-icon
      >
        <el-button v-if="loadRetryable" size="small" @click="reload">重试</el-button>
      </el-alert>

      <template v-else>
        <el-table
          :data="units"
          stripe
          border
          scrollbar-tabindex="0"
          aria-label="单元列表"
          class="unit-dialog__table"
        >
          <el-table-column prop="number" label="编号" width="80" />
          <el-table-column label="标题" min-width="160">
            <template #default="{ row }">
              {{ row.title ?? '—' }}
            </template>
          </el-table-column>
          <el-table-column prop="meaningCount" label="关联词义数" width="110" />
          <el-table-column label="操作" width="130" fixed="right">
            <template #default="{ row }">
              <el-button link type="primary" @click="startEdit(row)">编辑</el-button>
              <el-button link type="danger" @click="remove(row)">删除</el-button>
            </template>
          </el-table-column>
          <template #empty>
            <el-empty :image-size="60" description="还没有单元，请在下方添加" />
          </template>
        </el-table>

        <el-form class="unit-dialog__form" label-width="64px" @submit.prevent="submit">
          <h3 class="unit-dialog__form-title">
            {{ editing ? `编辑单元 ${editing.number}` : '添加单元' }}
          </h3>
          <el-form-item label="编号">
            <el-input-number
              v-model="formNumber"
              :min="1"
              :step="1"
              step-strictly
              :disabled="submitting"
              aria-label="单元编号"
            />
          </el-form-item>
          <el-form-item label="标题">
            <el-input
              v-model="formTitle"
              :disabled="submitting"
              placeholder="可选，如：Unit 2 Daily Life"
              clearable
            />
          </el-form-item>
          <el-alert
            v-if="formError"
            class="unit-dialog__notice"
            type="error"
            :title="formError"
            :closable="false"
            show-icon
          >
            <el-button v-if="formStale" size="small" @click="reload">刷新列表</el-button>
          </el-alert>
          <div class="unit-dialog__actions">
            <el-button v-if="editing" :disabled="submitting" @click="resetForm">取消编辑</el-button>
            <el-button type="primary" :loading="submitting" @click="submit">
              {{ editing ? '保存修改' : '添加单元' }}
            </el-button>
          </div>
        </el-form>
      </template>
    </div>

    <template #footer>
      <el-button @click="open = false">关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.unit-dialog__body {
  min-height: 120px;
}

.unit-dialog__table {
  margin-bottom: var(--lx-space-4);
}

.unit-dialog__form-title {
  margin: 0 0 var(--lx-space-3);
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-sm);
  font-weight: 600;
}

.unit-dialog__notice {
  margin-bottom: var(--lx-space-2);
}

.unit-dialog__actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--lx-space-2);
}
</style>
