<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import {
  commitAdminBookCleanup,
  previewAdminBookCleanup
} from '@/services/adminVocabularyApi'
import type {
  AdminCleanupCommit,
  AdminCleanupPreview,
  AdminCleanupResult,
  AdminCleanupSelection
} from '@/services/adminVocabularyApi'
import { getApiError } from '@/services/apiError'

/**
 * The one preview-and-confirm flow every cleanup entry shares: the book list's
 * clear and delete, the book word list's removals, and the detail drawer's
 * meaning deletion. Opening always fetches a fresh, read-only preview — the
 * counts it shows are estimates, and the text says so. Nothing is committed
 * until 确认清理 is pressed; a delete also requires typing the exact book name.
 * Cancelling ends the preview only. A refused commit keeps the dialog so the
 * administrator decides the next step; a commit whose answer never arrived is
 * an unknown outcome: the surroundings are asked to re-query state and the
 * commit is never replayed by this dialog.
 */

const props = defineProps<{
  bookId: string | null
  selection: AdminCleanupSelection | null
}>()

const open = defineModel<boolean>({ required: true })

const emit = defineEmits<{
  /** The transaction committed; the counts are the server's actual ones. */
  committed: [result: AdminCleanupResult]
  /** A commit was sent but its answer never arrived; re-query, do not replay. */
  unknown: []
}>()

const preview = ref<AdminCleanupPreview | null>(null)
const previewing = ref(false)
const previewError = ref('')
/** Set when re-running the preview is safe and useful (stale scope, busy, network). */
const previewRetryable = ref(false)
const confirmedName = ref('')
const committing = ref(false)
const commitError = ref('')
/** Set when the commit was refused in a way that needs a fresh preview. */
const commitStale = ref(false)

const actionLabels: Record<AdminCleanupSelection['action'], string> = {
  removeMeaning: '删除这条释义',
  removeWords: '从本教材移除所选单词',
  clear: '清空教材内容',
  delete: '删除教材及其内容'
}

const actionLabel = computed(() =>
  props.selection ? actionLabels[props.selection.action] : ''
)

const title = computed(() => (actionLabel.value ? `确认${actionLabel.value}` : '确认清理'))

const nameMatches = computed(() => {
  if (props.selection?.action !== 'delete') {
    return true
  }

  return confirmedName.value === (preview.value?.bookName ?? null)
})

const canCommit = computed(() => Boolean(preview.value) && !committing.value && nameMatches.value)

watch(
  () => [open.value, props.bookId, props.selection] as const,
  () => {
    if (open.value && props.bookId && props.selection) {
      runPreview()
    }
  }
)

function reset() {
  preview.value = null
  previewError.value = ''
  previewRetryable.value = false
  confirmedName.value = ''
  committing.value = false
  commitError.value = ''
  commitStale.value = false
}

function runPreview() {
  if (!props.bookId || !props.selection || previewing.value) {
    return
  }

  reset()
  previewing.value = true
  previewAdminBookCleanup(props.bookId, props.selection).then(
    (data) => {
      previewing.value = false
      preview.value = data
    },
    (error: unknown) => {
      previewing.value = false
      const apiError = getApiError(error)
      if (apiError.status === 404) {
        previewError.value = '目标教材不存在或已被删除。'
      } else if (apiError.status === 409) {
        previewRetryable.value = true
        previewError.value = `所选内容已发生变化，不能按原范围清理：${apiError.message} 请重新预览。`
      } else if (apiError.status === undefined) {
        previewRetryable.value = true
        previewError.value = '网络异常，未能取得预览；预览只读不写入，可重试。'
      } else if (apiError.status === 503) {
        previewRetryable.value = true
        previewError.value = '服务忙，暂时无法取得预览，请稍后重试。'
      } else {
        previewError.value = `预览失败：${apiError.message}`
      }
    }
  )
}

function cancel() {
  open.value = false
}

async function commit() {
  if (!props.bookId || !props.selection || !preview.value || committing.value) {
    return
  }

  const scope: AdminCleanupCommit =
    props.selection.action === 'delete'
      ? { action: 'delete', confirmedBookName: confirmedName.value }
      : props.selection

  commitError.value = ''
  commitStale.value = false
  committing.value = true
  try {
    const result = await commitAdminBookCleanup(props.bookId, scope)
    emit('committed', result)
    open.value = false
    return
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === undefined) {
      // The write was sent and never answered: the outcome is unknown. Ask the
      // surroundings to re-query current state; this dialog never resends.
      ElMessage.warning('清理提交结果未知，请查看当前状态后再决定，不要盲目重复提交')
      emit('unknown')
      open.value = false
      return
    }

    if (apiError.status === 404) {
      commitError.value = '目标教材或所选内容不存在或已被删除，请重新预览。'
      commitStale.value = true
    } else if (apiError.status === 409) {
      commitError.value = `清理未执行：${apiError.message} 所选范围或教材名称与当前数据不一致，请重新预览确认。`
      commitStale.value = true
    } else {
      commitError.value = `清理失败：${apiError.message}`
    }
  } finally {
    committing.value = false
  }
}
</script>

<template>
  <el-dialog
    v-model="open"
    class="cleanup-dialog"
    :title="title"
    width="min(480px, calc(100vw - 32px))"
    :close-on-click-modal="false"
    @closed="reset"
  >
    <div v-loading="previewing" class="cleanup-dialog__body">
      <el-alert
        v-if="previewError"
        class="cleanup-dialog__notice"
        type="error"
        :title="previewError"
        :closable="false"
        show-icon
      >
        <el-button v-if="previewRetryable" size="small" @click="runPreview">
          重新预览
        </el-button>
      </el-alert>

      <template v-else-if="preview">
        <p class="cleanup-dialog__line">
          目标教材：<span class="cleanup-dialog__book">{{ preview.bookName }}</span>
        </p>
        <p class="cleanup-dialog__line">清理操作：{{ actionLabel }}</p>
        <ul class="cleanup-dialog__counts">
          <li>涉及去重单词 {{ preview.affectedWordCount }} 个</li>
          <li>涉及释义 {{ preview.meaningCount }} 条</li>
          <li>预计 {{ preview.orphanWordCount }} 个单词将因此失去全部教材引用并被删除</li>
        </ul>
        <el-alert
          class="cleanup-dialog__notice"
          type="warning"
          title="以上计数为估计值，提交时按教材当前内容重新校验并执行。"
          :closable="false"
          show-icon
        />
        <el-alert
          class="cleanup-dialog__notice"
          type="info"
          title="其他教材（包括停用教材）中的释义与共享单词会保留；本操作不可撤销。"
          :closable="false"
          show-icon
        />

        <el-form v-if="selection?.action === 'delete'" label-position="top" @submit.prevent="commit">
          <el-form-item :label="`请输入教材名称「${preview.bookName}」以确认`">
            <el-input v-model="confirmedName" :disabled="committing" autocomplete="off" />
          </el-form-item>
        </el-form>

        <el-alert
          v-if="commitError"
          class="cleanup-dialog__notice"
          type="error"
          :title="commitError"
          :closable="false"
          show-icon
        >
          <el-button v-if="commitStale" size="small" @click="runPreview">重新预览</el-button>
        </el-alert>
      </template>
    </div>

    <template #footer>
      <el-button :disabled="committing" @click="cancel">取消</el-button>
      <el-button
        type="danger"
        :loading="committing"
        :disabled="!canCommit"
        @click="commit"
      >
        确认清理
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.cleanup-dialog__body {
  min-height: 80px;
}

.cleanup-dialog__line {
  margin: 0 0 var(--lx-space-2);
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-sm);
}
.cleanup-dialog__book {
  font-weight: 600;
  word-break: break-all;
}

.cleanup-dialog__counts {
  margin: 0 0 var(--lx-space-3);
  padding-left: var(--lx-space-4);
  color: var(--lx-color-text-regular);
  font-size: var(--lx-font-size-sm);
}

.cleanup-dialog__notice + .cleanup-dialog__notice {
  margin-top: var(--lx-space-2);
}
.cleanup-dialog__notice {
  margin-bottom: var(--lx-space-2);
}
.cleanup-dialog__notice:last-child {
  margin-bottom: 0;
}

.cleanup-dialog :deep(.el-form-item) {
  margin-top: var(--lx-space-3);
  margin-bottom: 0;
}
</style>
