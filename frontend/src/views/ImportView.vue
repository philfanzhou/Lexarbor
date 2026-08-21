<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import type { FormInstance, FormRules } from 'element-plus'
import { getActiveBooks } from '@/services/bookApi'
import { addVocabulary } from '@/services/vocabularyApi'
import { getApiError } from '@/services/apiError'
import type { Book } from '@/types'

const formRef = ref<FormInstance>()
const books = ref<Book[]>([])
const loading = ref(false)

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
  } catch (error: unknown) {
    ElMessage.error(getApiError(error).message)
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
    resetForm()
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

onMounted(loadBooks)
</script>

<template>
  <div class="import-view">
    <el-card shadow="never">
      <template #header>
        <span class="card-title">单词导入</span>
      </template>
      <el-form
        ref="formRef"
        v-loading="loading"
        :model="form"
        :rules="rules"
        label-width="80px"
        style="max-width: 480px"
      >
        <el-form-item label="教材" prop="bookId">
          <el-select v-model="form.bookId" placeholder="请选择教材" style="width: 100%">
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
        <el-form-item label="英式音标" prop="phoneticUk">
          <el-input v-model="form.phoneticUk" placeholder="如：/ˈæp.əl/" />
        </el-form-item>
        <el-form-item label="美式音标" prop="phoneticUs">
          <el-input v-model="form.phoneticUs" placeholder="如：/ˈæp.əl/" />
        </el-form-item>
        <el-form-item label="词性" prop="partOfSpeech">
          <el-select v-model="form.partOfSpeech" placeholder="请选择词性" clearable style="width: 100%">
            <el-option v-for="pos in partOfSpeechOptions" :key="pos" :label="pos" :value="pos" />
          </el-select>
        </el-form-item>
        <el-form-item label="释义" prop="meaning">
          <el-input v-model="form.meaning" placeholder="如：苹果" />
        </el-form-item>
        <el-form-item label="例句" prop="example">
          <el-input v-model="form.example" type="textarea" :rows="3" placeholder="如：I eat an apple." />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" @click="handleSubmit">导入</el-button>
          <el-button @click="resetForm">清空</el-button>
        </el-form-item>
      </el-form>
    </el-card>
  </div>
</template>

<style scoped>
.import-view { padding: 16px; }
.card-title { font-weight: 600; font-size: 16px; }
</style>
