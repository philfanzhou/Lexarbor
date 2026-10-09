<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { FormInstance, FormRules } from 'element-plus'
import { getActiveBooks, getBookUnits } from '@/services/bookApi'
import { importVocabularyBatch } from '@/services/vocabularyApi'
import { getApiError } from '@/services/apiError'
import PageHeader from '@/components/PageHeader.vue'
import type { Book, BookUnit } from '@/types'

const formRef = ref<FormInstance>()
const booksLoading = ref(false)
const unknownResult = ref(false)
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
let bookGeneration = 0
let alive = true
const rules: FormRules = {
  bookId: [{ required: true, message: '请选择教材', trigger: 'change' }],
  unitId: [{ required: true, message: '请选择单元', trigger: 'change' }],
  word: [{ required: true, whitespace: true, message: '请输入英文短语', trigger: 'blur' }],
  meaning: [{ required: true, whitespace: true, message: '请输入释义', trigger: 'blur' }]
}
const partOfSpeechOptions = ['n.', 'v.', 'adj.', 'adv.', 'prep.', 'conj.', 'pron.', 'int.', 'art.']

function clearContent(clearFeedback = true) {
  if (submitting.value) return
  Object.assign(form.value, { word: '', meaning: '', phoneticUk: '', phoneticUs: '', partOfSpeech: '', example: '' })
  formRef.value?.clearValidate()
  if (clearFeedback) { error.value = ''; success.value = ''; unknownResult.value = false }
}

async function loadBooks() {
  if (submitting.value) return
  const generation = ++bookGeneration
  booksLoading.value = true
  booksError.value = ''
  try {
    const result = await getActiveBooks()
    if (alive && generation === bookGeneration) books.value = result.books
  } catch (cause: unknown) {
    if (alive && generation === bookGeneration) booksError.value = getApiError(cause).message
  } finally {
    if (alive && generation === bookGeneration) booksLoading.value = false
  }
}

async function loadUnits(bookId: string) {
  if (submitting.value) return
  const generation = ++unitGeneration
  units.value = []
  unitsError.value = ''
  unitsLoading.value = false
  if (!bookId) return
  unitsLoading.value = true
  try {
    const result = await getBookUnits(bookId)
    if (alive && generation === unitGeneration) units.value = result.units
  } catch (cause: unknown) {
    if (alive && generation === unitGeneration) unitsError.value = getApiError(cause).message
  } finally {
    if (alive && generation === unitGeneration) unitsLoading.value = false
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
  unknownResult.value = false
  // Lock before asynchronous validation, so two activations cannot send two writes.
  submitting.value = true
  const valid = await formRef.value?.validate().catch(() => false)
  if (!alive) return
  if (!valid) {
    submitting.value = false
    error.value = '请选择教材和单元，并填写英文短语与释义。'
    await nextTick()
    formRef.value?.$el.querySelector('.is-error input')?.focus()
    return
  }
  if (booksLoading.value || booksError.value || unitsLoading.value || unitsError.value || !units.value.some(unit => unit.id === form.value.unitId)) {
    error.value = '请重新加载并选择该教材的单元。'
    submitting.value = false
    return
  }
  formRef.value?.clearValidate()
  const snapshot = { ...form.value }
  const book = books.value.find(book => book.id === snapshot.bookId)
  const unit = units.value.find(unit => unit.id === snapshot.unitId)
  const location = `${book?.bookName} / 第 ${unit?.number} 单元${snapshot.section ? ` / Section ${snapshot.section}` : ' / 未分节'}`
  try {
    const result = await importVocabularyBatch({
      bookId: snapshot.bookId,
      entries: [{
        word: snapshot.word.trim(), meaning: snapshot.meaning.trim(), unitId: snapshot.unitId,
        section: snapshot.section || undefined, entryKind: 'phrase',
        phoneticUk: snapshot.phoneticUk.trim() || undefined,
        phoneticUs: snapshot.phoneticUs.trim() || undefined,
        partOfSpeech: snapshot.partOfSpeech.trim() || undefined, example: snapshot.example.trim() || undefined
      }]
    })
    if (!alive) return
    submitting.value = false
    clearContent(false)
    success.value = `「${snapshot.word.trim()}」 · ${location}：` + (result.created === 1
      ? '已新增短语词义并放入所选单元。'
      : result.reused === 1
        ? '已复用现有词义并放入所选单元；复用数统计的是词义数量，不是在单元、分节、类别中新增的关联数量，已存在的关联不会重复创建。'
        : '短语已处理。')
  } catch (cause: unknown) {
    if (!alive) return
    const failure = getApiError(cause)
    unknownResult.value = failure.status === undefined
    error.value = unknownResult.value
      ? '提交结果未知。请先查询当前短语位置，再决定是否重试。'
      : failure.status === 404 ? '所选教材或单元不存在，请重新加载后选择。'
        : failure.status === 422 ? `教材已停用或内容不符合业务规则：${failure.message}`
          : failure.errors?.[0]?.message ?? failure.message
  } finally {
    if (alive) submitting.value = false
  }
}

onBeforeUnmount(() => { alive = false; ++bookGeneration; ++unitGeneration })
onMounted(() => { void loadBooks() })
</script>

<template>
  <section class="phrase-import">
    <PageHeader title="新增短语" description="为教材中的一个单元新增短语位置；类别固定为短语" />
    <el-alert v-if="booksError" class="phrase-import__alert" type="error" :title="`教材加载失败：${booksError}`" :closable="false" show-icon>
      <el-button :disabled="submitting" @click="loadBooks">重试教材列表</el-button>
    </el-alert>
    <el-alert v-if="unitsError" class="phrase-import__alert" type="error" :title="`单元加载失败：${unitsError}`" :closable="false" show-icon>
      <el-button :disabled="submitting" @click="loadUnits(form.bookId)">重试单元列表</el-button>
    </el-alert>
    <el-alert v-if="error" class="phrase-import__alert" type="error" :title="error" :closable="false" show-icon>
      <RouterLink v-if="unknownResult" to="/phrases">前往短语管理核对</RouterLink>
    </el-alert>
    <div v-if="success" role="status"><el-alert class="phrase-import__alert" type="success" :title="success" :closable="false" show-icon /></div>
    <el-card class="phrase-import__card" shadow="never">
      <p class="phrase-import__required">带 * 的为必填项；清空内容保留当前教材与位置。</p>
      <el-form ref="formRef" class="phrase-import__form" label-position="top" :model="form" :rules="rules" :disabled="submitting" @submit.prevent="submit">
        <section class="phrase-import__group">
          <h2>教材与位置</h2>
          <el-form-item label="教材" prop="bookId">
            <el-select v-model="form.bookId" class="phrase-import__book" placeholder="选择启用教材" :disabled="submitting || booksLoading || !!booksError">
              <el-option v-for="book in books" :key="book.id" :label="book.bookName" :value="book.id" />
            </el-select>
          </el-form-item>
          <el-form-item label="单元" prop="unitId">
            <el-select v-model="form.unitId" class="phrase-import__unit" placeholder="选择现有单元" :disabled="submitting || !form.bookId || unitsLoading || !!unitsError || !units.length">
              <el-option v-for="unit in units" :key="unit.id" :label="`第 ${unit.number} 单元 ${unit.title ?? ''}`" :value="unit.id" />
            </el-select>
          </el-form-item>
          <el-alert v-if="form.bookId && !unitsLoading && !unitsError && !units.length" type="info" title="该教材尚未建立单元，请先在教材管理中新增单元。" :closable="false">
            <RouterLink to="/books">前往教材管理</RouterLink>
          </el-alert>
          <el-form-item label="分节">
            <el-select v-model="form.section" class="phrase-import__section" :disabled="submitting || !form.unitId">
              <el-option label="未分节" value="" /><el-option label="Section A" value="A" /><el-option label="Section B" value="B" />
            </el-select>
          </el-form-item>
        </section>
        <section class="phrase-import__group">
          <h2>基本信息</h2>
          <el-form-item label="类别"><el-tag>短语</el-tag></el-form-item>
          <el-form-item label="英文短语" prop="word"><el-input v-model="form.word" placeholder="如：take off" /></el-form-item>
        </section>
        <section class="phrase-import__group">
          <h2>发音</h2>
          <div class="phrase-import__columns">
            <el-form-item label="英式音标"><el-input v-model="form.phoneticUk" /></el-form-item>
            <el-form-item label="美式音标"><el-input v-model="form.phoneticUs" /></el-form-item>
          </div>
        </section>
        <section class="phrase-import__group">
          <h2>释义与例句</h2>
          <el-form-item label="词性">
            <el-select v-model="form.partOfSpeech" filterable allow-create default-first-option clearable placeholder="选择或输入词性">
              <el-option v-for="pos in partOfSpeechOptions" :key="pos" :label="pos" :value="pos" />
            </el-select>
          </el-form-item>
          <el-form-item label="释义" prop="meaning"><el-input v-model="form.meaning" placeholder="如：起飞" /></el-form-item>
          <el-form-item label="例句"><el-input v-model="form.example" type="textarea" :rows="3" /></el-form-item>
        </section>
        <div class="phrase-import__actions">
          <el-button type="primary" native-type="submit" :loading="submitting" :disabled="submitting || booksLoading || !!booksError || unitsLoading || !!unitsError || (!!form.bookId && !units.length)">新增短语</el-button>
          <el-button @click="clearContent()">清空内容</el-button>
        </div>
      </el-form>
    </el-card>
  </section>
</template>

<style scoped>
.phrase-import__card, .phrase-import__alert { max-width: 720px; }
.phrase-import__alert { margin-bottom: var(--lx-space-4); }
.phrase-import__card { border-color: var(--lx-color-border-light); border-radius: var(--lx-radius-md); box-shadow: var(--lx-shadow-1); }
.phrase-import__card :deep(.el-card__body) { padding: var(--lx-space-5); }
.phrase-import__required { margin: 0 0 var(--lx-space-4); color: var(--lx-color-text-secondary); font-size: var(--lx-font-size-sm); }
.phrase-import__form :deep(.el-select) { width: 100%; }
.phrase-import__group + .phrase-import__group, .phrase-import__actions { padding-top: var(--lx-space-4); border-top: 1px solid var(--lx-color-border-light); }
.phrase-import__group h2 { margin: 0 0 var(--lx-space-3); font-size: var(--lx-font-size-md); }
.phrase-import__columns { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--lx-space-4); }
@media (min-width: 1024px) { .phrase-import__columns { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
</style>
