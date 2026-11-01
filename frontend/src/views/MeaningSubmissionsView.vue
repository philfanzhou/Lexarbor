<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import PageHeader from '@/components/PageHeader.vue'
import { getApiError } from '@/services/apiError'
import {
  adviseMeaningSubmission,
  approveMeaningSubmission,
  listMeaningSubmissions,
  rejectMeaningSubmission
} from '@/services/meaningSubmissionApi'
import type {
  MeaningSubmissionAdvice,
  MeaningSubmissionItem,
  MeaningSubmissionStatus
} from '@/services/meaningSubmissionApi'

const pageSize = 20
const status = ref<MeaningSubmissionStatus>('pending')
const page = ref(1)
const items = ref<MeaningSubmissionItem[]>([])
const totalCount = ref(0)
const configured = ref(true)
const assistantEnabled = ref(false)
const loading = ref(false)
const error = ref('')

const reviewing = ref<MeaningSubmissionItem | null>(null)
const form = reactive({ word: '', partOfSpeech: '', meaning: '', example: '', phoneticUk: '', phoneticUs: '' })
const advice = ref<MeaningSubmissionAdvice | null>(null)
const advising = ref(false)
const saving = ref(false)

const verdictLabel = computed(() => {
  switch (advice.value?.verdict) {
    case 'accept': return '建议通过'
    case 'revise': return '建议修改后通过'
    case 'reject': return '建议驳回'
    default: return ''
  }
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    const result = await listMeaningSubmissions(status.value, page.value, pageSize)
    items.value = result.items
    totalCount.value = result.totalCount
    configured.value = result.configured
    assistantEnabled.value = result.assistantEnabled
  } catch (failure: unknown) {
    error.value = getApiError(failure).message
  } finally {
    loading.value = false
  }
}

watch(status, () => {
  page.value = 1
  void load()
})

function openReview(item: MeaningSubmissionItem) {
  const submission = item.submission
  reviewing.value = item
  advice.value = null
  Object.assign(form, {
    word: submission.word,
    partOfSpeech: submission.partOfSpeech ?? '',
    meaning: submission.meaning,
    example: submission.example ?? '',
    phoneticUk: '',
    phoneticUs: ''
  })
}

async function requestAdvice() {
  if (!reviewing.value) return
  advising.value = true
  try {
    advice.value = await adviseMeaningSubmission(reviewing.value.submission.id)
  } catch (failure: unknown) {
    ElMessage.error(`AI 审核失败：${getApiError(failure).message}`)
  } finally {
    advising.value = false
  }
}

function applyAdvice() {
  const suggestion = advice.value
  if (!suggestion) return
  if (suggestion.partOfSpeech) form.partOfSpeech = suggestion.partOfSpeech
  if (suggestion.meaning) form.meaning = suggestion.meaning
  if (suggestion.phoneticUk) form.phoneticUk = suggestion.phoneticUk
  if (suggestion.phoneticUs) form.phoneticUs = suggestion.phoneticUs
}

async function approve() {
  if (!reviewing.value) return
  if (!form.word.trim() || !form.meaning.trim()) {
    ElMessage.warning('单词和释义不能为空')
    return
  }
  saving.value = true
  try {
    await approveMeaningSubmission(reviewing.value.submission.id, { ...form })
    ElMessage.success('已通过，释义已写入补充词书')
    reviewing.value = null
    await load()
  } catch (failure: unknown) {
    ElMessage.error(`通过失败：${getApiError(failure).message}`)
  } finally {
    saving.value = false
  }
}

async function reject(item: MeaningSubmissionItem) {
  try {
    await ElMessageBox.confirm(`驳回「${item.submission.word}：${item.submission.meaning}」？驳回后不能撤销。`, '驳回投稿', {
      confirmButtonText: '驳回',
      cancelButtonText: '取消',
      type: 'warning'
    })
  } catch {
    return
  }
  try {
    await rejectMeaningSubmission(item.submission.id)
    ElMessage.success('已驳回')
    if (reviewing.value?.submission.id === item.submission.id) reviewing.value = null
    await load()
  } catch (failure: unknown) {
    ElMessage.error(`驳回失败：${getApiError(failure).message}`)
  }
}

function changePage(next: number) {
  page.value = next
  void load()
}

function formatTime(value?: string | null) {
  return value ? new Date(value).toLocaleString() : ''
}

onMounted(load)
</script>

<template>
  <section class="submissions">
    <PageHeader title="释义投稿审核" description="词书里都没有的释义由接入方投稿，通过后写入补充词书。" />
    <el-alert
      v-if="!configured"
      class="submissions__alert"
      type="warning"
      title="未配置补充词书（MeaningSubmissions:BookId），投稿与审核暂不可用。"
      :closable="false"
      show-icon
    />
    <el-radio-group v-model="status" aria-label="投稿状态">
      <el-radio-button value="pending">待审核</el-radio-button>
      <el-radio-button value="approved">已通过</el-radio-button>
      <el-radio-button value="rejected">已驳回</el-radio-button>
    </el-radio-group>
    <el-alert v-if="error" class="submissions__alert" type="error" :title="`加载失败：${error}`" :closable="false" show-icon>
      <el-button @click="load">重试</el-button>
    </el-alert>
    <template v-else>
      <p role="status">{{ loading ? '正在加载投稿…' : `共 ${totalCount} 条` }}</p>
      <el-table v-loading="loading" :data="items" row-key="submission.id" empty-text="没有投稿">
        <el-table-column label="单词" min-width="140">
          <template #default="{ row }">{{ row.submission.word }}</template>
        </el-table-column>
        <el-table-column label="词性" width="90">
          <template #default="{ row }">{{ row.submission.partOfSpeech ?? '' }}</template>
        </el-table-column>
        <el-table-column label="投稿释义" min-width="200">
          <template #default="{ row }">{{ row.submission.meaning }}</template>
        </el-table-column>
        <el-table-column v-if="status === 'approved'" label="通过后的释义" min-width="200">
          <template #default="{ row }">
            {{ row.submission.resultWord }} {{ row.submission.resultPartOfSpeech ?? '' }} {{ row.submission.resultMeaning }}
            <el-tag v-if="!row.submission.definitionKept" size="small" type="warning">已修改</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="来源" width="160">
          <template #default="{ row }">{{ row.source }}</template>
        </el-table-column>
        <el-table-column :label="status === 'pending' ? '提交时间' : '审核时间'" width="180">
          <template #default="{ row }">{{ formatTime(status === 'pending' ? row.submission.createdAt : row.submission.reviewedAt) }}</template>
        </el-table-column>
        <el-table-column v-if="status === 'pending'" label="操作" width="160" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" :disabled="!configured" @click="openReview(row)">审核</el-button>
            <el-button link type="danger" @click="reject(row)">驳回</el-button>
          </template>
        </el-table-column>
      </el-table>
      <el-pagination
        v-if="totalCount > pageSize"
        class="submissions__pagination"
        layout="prev, pager, next"
        :total="totalCount"
        :page-size="pageSize"
        :current-page="page"
        @current-change="changePage"
      />
    </template>

    <el-dialog
      :model-value="reviewing !== null"
      title="审核投稿"
      width="560px"
      @close="reviewing = null"
    >
      <template v-if="reviewing">
        <p class="submissions__original">
          投稿：{{ reviewing.submission.word }} {{ reviewing.submission.partOfSpeech ?? '' }} {{ reviewing.submission.meaning }}
        </p>
        <el-form label-width="88px" @submit.prevent>
          <el-form-item label="单词" required><el-input v-model="form.word" maxlength="100" /></el-form-item>
          <el-form-item label="词性"><el-input v-model="form.partOfSpeech" maxlength="32" placeholder="如 n. / v." /></el-form-item>
          <el-form-item label="释义" required><el-input v-model="form.meaning" maxlength="500" /></el-form-item>
          <el-form-item label="例句"><el-input v-model="form.example" maxlength="1000" type="textarea" /></el-form-item>
          <el-form-item label="英式音标"><el-input v-model="form.phoneticUk" maxlength="100" /></el-form-item>
          <el-form-item label="美式音标"><el-input v-model="form.phoneticUs" maxlength="100" /></el-form-item>
        </el-form>
        <el-alert
          v-if="advice"
          class="submissions__advice"
          :type="advice.verdict === 'reject' ? 'error' : advice.verdict === 'revise' ? 'warning' : 'success'"
          :title="verdictLabel"
          :closable="false"
          show-icon
        >
          <p v-if="advice.comment">{{ advice.comment }}</p>
          <p v-if="advice.meaning">建议释义：{{ advice.partOfSpeech ?? '' }} {{ advice.meaning }}</p>
          <p v-if="advice.phoneticUk || advice.phoneticUs">建议音标：{{ advice.phoneticUk ?? '' }} / {{ advice.phoneticUs ?? '' }}</p>
          <el-button size="small" @click="applyAdvice">采用建议</el-button>
        </el-alert>
        <p class="submissions__hint">通过后可在「单词管理」为这个词上传发音。修改释义文字会让已经在学这条释义的学习者重新学一次。</p>
      </template>
      <template #footer>
        <el-button v-if="assistantEnabled" :loading="advising" @click="requestAdvice">AI 审核建议</el-button>
        <el-button v-if="reviewing" type="danger" plain @click="reject(reviewing)">驳回</el-button>
        <el-button type="primary" :loading="saving" @click="approve">通过</el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.submissions__alert {
  margin-bottom: var(--lx-space-4);
}
.submissions__pagination {
  margin-top: var(--lx-space-4);
}
.submissions__original {
  margin-top: 0;
  color: var(--lx-color-text-secondary);
}
.submissions__advice {
  margin-top: var(--lx-space-3);
}
.submissions__hint {
  margin-bottom: 0;
  color: var(--lx-color-text-secondary);
  font-size: 13px;
}
</style>
