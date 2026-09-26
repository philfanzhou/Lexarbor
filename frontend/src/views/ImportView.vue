<script setup lang="ts">
import { ref, onMounted, nextTick, watch } from 'vue'
import { ElMessage } from 'element-plus'
import type { FormInstance, FormRules } from 'element-plus'
import { getActiveBooks } from '@/services/bookApi'
import { addVocabulary } from '@/services/vocabularyApi'
import { getApiError } from '@/services/apiError'
import PageHeader from '@/components/PageHeader.vue'
import type { Book } from '@/types'

const formRef = ref<FormInstance>()
const books = ref<Book[]>([])
const loading = ref(false)

// For display only: the word just imported, and whether the book list failed.
const lastImported = ref('')
const booksError = ref('')

const form = ref({
  bookId: '',
  word: '',
  phoneticUk: '',
  phoneticUs: '',
  partOfSpeech: '',
  meaning: '',
  example: ''
})

const rules: FormRules = {
  bookId: [{ required: true, message: '请选择教材', trigger: 'change' }],
  word: [{ required: true, message: '请输入单词', trigger: 'blur' }],
  meaning: [{ required: true, message: '请输入释义', trigger: 'blur' }]
}

const partOfSpeechOptions = [
  'n.', 'v.', 'adj.', 'adv.', 'prep.', 'conj.', 'pron.', 'int.', 'art.'
]

async function loadBooks() {
  try {
    const data = await getActiveBooks()
    books.value = data.books
    booksError.value = ''
  } catch (error: unknown) {
    const message = getApiError(error).message
    ElMessage.error(message)
    booksError.value = message
  }
}

function resetForm() {
  form.value = {
    bookId: '',
    word: '',
    phoneticUk: '',
    phoneticUs: '',
    partOfSpeech: '',
    meaning: '',
    example: ''
  }
}

async function handleSubmit() {
  await formRef.value?.validate()
  loading.value = true
  try {
    await addVocabulary({
      word: {
        word: form.value.word,
        phoneticUk: form.value.phoneticUk || undefined,
        phoneticUs: form.value.phoneticUs || undefined
      },
      meaning: {
        bookId: form.value.bookId,
        partOfSpeech: form.value.partOfSpeech || undefined,
        meaning: form.value.meaning,
        example: form.value.example || undefined
      }
    })
    ElMessage.success('导入成功')
    const importedWord = form.value.word
    resetForm()
    // Set after the reset has reached the watcher below, which would clear it.
    await nextTick()
    lastImported.value = importedWord
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === 404) {
      ElMessage.error('所选教材不存在，请刷新后重新选择')
    } else if (apiError.status === 409) {
      ElMessage.error('该单词或词义与现有数据冲突')
    } else if (apiError.status === 422) {
      ElMessage.error('导入内容不符合业务规则')
    } else {
      ElMessage.error(apiError.message)
    }
  } finally {
    loading.value = false
  }
}

watch(form, () => {
  lastImported.value = ''
}, { deep: true })

onMounted(loadBooks)
</script>

<template>
  <div class="import-view">
    <PageHeader title="单条导入" description="向一本教材添加一个单词和它的一条释义" />

    <el-alert
      v-if="booksError"
      class="import-alert"
      type="error"
      :title="booksError"
      :closable="false"
      show-icon
    >
      <el-button class="import-alert__retry" size="small" @click="loadBooks">重试</el-button>
    </el-alert>
    <el-alert
      v-if="lastImported"
      class="import-alert"
      type="success"
      :title="`已导入「${lastImported}」`"
      show-icon
      @close="lastImported = ''"
    />

    <el-card class="import-card" shadow="never">
      <p class="import-required">带 * 的为必填项</p>
      <el-form
        ref="formRef"
        v-loading="loading"
        class="import-form"
        :model="form"
        :rules="rules"
        label-position="top"
      >
        <section class="import-section">
          <h2 class="import-section__title">基本信息</h2>
          <el-form-item label="教材" prop="bookId">
            <el-select v-model="form.bookId" placeholder="请选择教材">
              <el-option
                v-for="book in books"
                :key="book.id"
                :label="book.bookName"
                :value="book.id"
              />
            </el-select>
          </el-form-item>
          <el-form-item label="单词" prop="word">
            <el-input v-model="form.word" placeholder="如：apple" />
          </el-form-item>
        </section>

        <section class="import-section">
          <h2 class="import-section__title">发音</h2>
          <div class="import-section__columns">
            <el-form-item label="英式音标" prop="phoneticUk">
              <el-input v-model="form.phoneticUk" placeholder="如：/ˈæp.əl/" />
            </el-form-item>
            <el-form-item label="美式音标" prop="phoneticUs">
              <el-input v-model="form.phoneticUs" placeholder="如：/ˈæp.əl/" />
            </el-form-item>
          </div>
        </section>

        <section class="import-section">
          <h2 class="import-section__title">释义与例句</h2>
          <el-form-item label="词性" prop="partOfSpeech">
            <el-select v-model="form.partOfSpeech" placeholder="请选择词性" clearable>
              <el-option v-for="pos in partOfSpeechOptions" :key="pos" :label="pos" :value="pos" />
            </el-select>
          </el-form-item>
          <el-form-item label="释义" prop="meaning">
            <el-input v-model="form.meaning" placeholder="如：苹果" />
          </el-form-item>
          <el-form-item label="例句" prop="example">
            <el-input v-model="form.example" type="textarea" :rows="3" placeholder="如：I eat an apple." />
          </el-form-item>
        </section>

        <el-form-item class="import-actions">
          <el-button type="primary" @click="handleSubmit">导入</el-button>
          <el-button @click="resetForm">清空</el-button>
        </el-form-item>
      </el-form>
    </el-card>
  </div>
</template>

<style scoped>
.import-alert {
  max-width: 720px;
  margin-bottom: var(--lx-space-4);
}
.import-alert__retry {
  margin-top: var(--lx-space-2);
}

.import-card {
  max-width: 720px;
  border-color: var(--lx-color-border-light);
  border-radius: var(--lx-radius-md);
  box-shadow: var(--lx-shadow-1);
}
.import-card :deep(.el-card__body) {
  padding: var(--lx-space-5);
}

.import-required {
  margin: 0 0 var(--lx-space-4);
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-sm);
}

.import-form :deep(.el-select) {
  width: 100%;
}

.import-section + .import-section {
  margin-top: var(--lx-space-2);
  padding-top: var(--lx-space-4);
  border-top: 1px solid var(--lx-color-border-light);
}
.import-section__title {
  margin: 0 0 var(--lx-space-3);
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-md);
  font-weight: 600;
}
.import-section__columns {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  column-gap: var(--lx-space-4);
}

.import-actions {
  margin: var(--lx-space-2) 0 0;
  padding-top: var(--lx-space-4);
  border-top: 1px solid var(--lx-color-border-light);
}

@media (min-width: 1024px) {
  .import-section__columns {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
