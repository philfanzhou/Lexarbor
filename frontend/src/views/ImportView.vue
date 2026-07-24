<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import type { FormInstance, FormRules } from 'element-plus'
import { getBooks } from '@/services/bookApi'
import { addVocabulary } from '@/services/vocabularyApi'
import type { Book } from '@/types'

const formRef = ref<FormInstance>()
const books = ref<Book[]>([])
const loading = ref(false)

const form = ref({
  bookId: '',
  word: '',
  phonetic: '',
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
    const data = await getBooks()
    books.value = data.items
  } catch (e: any) {
    ElMessage.error(e.message)
  }
}

function resetForm() {
  form.value = { bookId: '', word: '', phonetic: '', partOfSpeech: '', meaning: '', example: '' }
}

async function handleSubmit() {
  await formRef.value?.validate()
  loading.value = true
  try {
    await addVocabulary({
      word: { word: form.value.word, phonetic: form.value.phonetic || undefined },
      meaning: {
        bookId: form.value.bookId,
        partOfSpeech: form.value.partOfSpeech || undefined,
        meaning: form.value.meaning,
        example: form.value.example || undefined
      }
    })
    ElMessage.success('导入成功')
    resetForm()
  } catch (e: any) {
    ElMessage.error(e.message)
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
        :model="form"
        :rules="rules"
        label-width="80px"
        v-loading="loading"
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
        <el-form-item label="音标" prop="phonetic">
          <el-input v-model="form.phonetic" placeholder="如：/ˈæp.əl/" />
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
