<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { getActiveBooks, getBookUnits } from '@/services/bookApi'
import { importVocabularyBatch } from '@/services/vocabularyApi'
import { getApiError } from '@/services/apiError'
import PageHeader from '@/components/PageHeader.vue'
import type { Book, BookUnit } from '@/types'

const books = ref<Book[]>([])
const booksError = ref('')
const units = ref<BookUnit[]>([])
const unitsError = ref('')
const unitsLoading = ref(false)
const submitting = ref(false)
const error = ref('')
const success = ref('')
const form = ref({
  bookId: '', unitId: '', section: '', word: '', phoneticUk: '', phoneticUs: '',
  partOfSpeech: '', meaning: '', example: ''
})
let unitGeneration = 0

async function loadBooks() {
  booksError.value = ''
  try {
    const result = await getActiveBooks()
    books.value = result.books
  } catch (cause: unknown) {
    booksError.value = getApiError(cause).message
  }
}

async function loadUnits(bookId: string) {
  const generation = ++unitGeneration
  units.value = []
  unitsError.value = ''
  if (!bookId) return
  unitsLoading.value = true
  try {
    const result = await getBookUnits(bookId)
    if (generation === unitGeneration) units.value = result.units
  } catch (cause: unknown) {
    if (generation === unitGeneration) unitsError.value = getApiError(cause).message
  } finally {
    if (generation === unitGeneration) unitsLoading.value = false
  }
}

watch(() => form.value.bookId, bookId => {
  form.value.unitId = ''
  form.value.section = ''
  error.value = ''
  success.value = ''
  void loadUnits(bookId)
})
watch(() => form.value.unitId, () => { form.value.section = ''; success.value = '' })

async function submit() {
  if (submitting.value) return
  error.value = ''
  success.value = ''
  if (!form.value.bookId || !form.value.unitId || !form.value.word.trim() || !form.value.meaning.trim()) {
    error.value = '请选择教材和单元，并填写英文短语与释义。'
    return
  }
  if (unitsLoading.value || unitsError.value || !units.value.some(unit => unit.id === form.value.unitId)) {
    error.value = '请重新加载并选择该教材的单元。'
    return
  }
  submitting.value = true
  try {
    const result = await importVocabularyBatch({
      bookId: form.value.bookId,
      entries: [{
        word: form.value.word.trim(),
        meaning: form.value.meaning.trim(),
        unitId: form.value.unitId,
        section: form.value.section || undefined,
        entryKind: 'phrase',
        phoneticUk: form.value.phoneticUk.trim() || undefined,
        phoneticUs: form.value.phoneticUs.trim() || undefined,
        partOfSpeech: form.value.partOfSpeech.trim() || undefined,
        example: form.value.example.trim() || undefined
      }]
    })
    success.value = result.created === 1
      ? '已新增短语词义和位置。'
      : result.reused === 1
        ? '已复用现有词义并确认短语位置；重复提交不会新增位置。'
        : '短语位置已处理。'
    form.value.word = ''
    form.value.meaning = ''
    form.value.phoneticUk = ''
    form.value.phoneticUs = ''
    form.value.partOfSpeech = ''
    form.value.example = ''
  } catch (cause: unknown) {
    const failure = getApiError(cause)
    error.value = failure.status === undefined
      ? '提交结果未知。请先查询当前短语位置，再决定是否重试。'
      : failure.errors?.[0]?.message ?? failure.message
  } finally {
    submitting.value = false
  }
}

onMounted(() => { void loadBooks() })
</script>

<template>
  <section class="phrase-import">
    <PageHeader title="新增短语" description="为教材中的一个单元新增短语位置；类别固定为短语" />
    <p v-if="booksError" role="alert">教材加载失败：{{ booksError }} <el-button @click="loadBooks">重试教材列表</el-button></p>
    <p v-if="unitsError" role="alert">单元加载失败：{{ unitsError }} <el-button @click="loadUnits(form.bookId)">重试单元列表</el-button></p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="success" role="status">{{ success }}</p>
    <el-form class="phrase-import__form" label-position="top" :model="form" @submit.prevent="submit">
      <el-form-item label="教材" required>
        <el-select v-model="form.bookId" class="phrase-import__book" placeholder="选择启用教材">
          <el-option v-for="book in books" :key="book.id" :label="book.bookName" :value="book.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="单元" required>
        <el-select v-model="form.unitId" class="phrase-import__unit" placeholder="选择现有单元" :disabled="!form.bookId || unitsLoading || !!unitsError">
          <el-option v-for="unit in units" :key="unit.id" :label="`第 ${unit.number} 单元 ${unit.title ?? ''}`" :value="unit.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="分节">
        <el-select v-model="form.section" class="phrase-import__section" :disabled="!form.unitId">
          <el-option label="未分节" value="" /><el-option label="Section A" value="A" /><el-option label="Section B" value="B" />
        </el-select>
      </el-form-item>
      <el-form-item label="类别"><el-tag>短语</el-tag></el-form-item>
      <el-form-item label="英文短语" required><el-input v-model="form.word" placeholder="如：take off" /></el-form-item>
      <el-form-item label="释义" required><el-input v-model="form.meaning" placeholder="如：起飞" /></el-form-item>
      <el-form-item label="英式音标"><el-input v-model="form.phoneticUk" /></el-form-item>
      <el-form-item label="美式音标"><el-input v-model="form.phoneticUs" /></el-form-item>
      <el-form-item label="词性"><el-input v-model="form.partOfSpeech" /></el-form-item>
      <el-form-item label="例句"><el-input v-model="form.example" type="textarea" /></el-form-item>
      <el-button type="primary" native-type="submit" :loading="submitting">新增短语</el-button>
    </el-form>
  </section>
</template>

<style scoped>
.phrase-import__form { max-width: 720px; }
.phrase-import__form :deep(.el-select) { width: 100%; }
</style>
