<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { getBookUnits } from '@/services/bookApi'
import { getApiError } from '@/services/apiError'
import { moveAdminMeaningPosition, removeAdminMeaningPosition } from '@/services/adminVocabularyApi'
import type { AdminMeaningPositionKey } from '@/services/adminVocabularyApi'
import type { BookUnit } from '@/types'

export interface PositionTarget {
  action: 'move' | 'remove'
  bookId: string
  bookName: string
  meaningId: string
  word: string
  meaning: string
  from: AdminMeaningPositionKey
  sourceNumber: number
  sourceTitle?: string | null
}

const target = defineModel<PositionTarget | null>({ required: true })
const emit = defineEmits<{ changed: []; unknown: [] }>()
const units = ref<BookUnit[]>([])
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const reloadHint = ref(false)
const mode = ref<'move' | 'remove'>('move')
const destination = ref<AdminMeaningPositionKey>({ unitId: '', section: null, entryKind: null })
let generation = 0

function unitLabel(id: string) {
  const unit = units.value.find(item => item.id === id)
  const number = unit?.number ?? (id === target.value?.from.unitId ? target.value.sourceNumber : null)
  const title = unit?.title ?? (id === target.value?.from.unitId ? target.value.sourceTitle : null)
  return number === null ? `单元 ${id}` : `第 ${number} 单元${title ? ` · ${title}` : ''}`
}
function sectionLabel(value: string | null) { return value ? `Section ${value}` : '未分节' }
function kindLabel(value: string | null) { return value === 'word' ? '词条' : value === 'phrase' ? '短语' : '未分类' }

async function loadUnits() {
  const current = generation
  const selected = target.value
  if (!selected) return
  loading.value = true
  error.value = ''
  try {
    const response = await getBookUnits(selected.bookId)
    if (current === generation) units.value = response.units
  } catch (cause: unknown) {
    if (current === generation) error.value = `单元加载失败：${getApiError(cause).message}`
  } finally {
    if (current === generation) loading.value = false
  }
}

watch(target, (selected) => {
  generation++
  units.value = []
  error.value = ''
  reloadHint.value = false
  saving.value = false
  if (selected) {
    destination.value = { ...selected.from }
    mode.value = selected.action
    if (selected.action === 'move') void loadUnits()
  }
})

async function submit() {
  const selected = target.value
  if (!selected || saving.value || (mode.value === 'move' && (loading.value || !units.value.some(unit => unit.id === destination.value.unitId)))) return
  const current = generation
  const from = { ...selected.from }
  const to = { ...destination.value }
  error.value = ''
  reloadHint.value = false
  saving.value = true
  try {
    if (mode.value === 'move') await moveAdminMeaningPosition(selected.bookId, selected.meaningId, { from, to })
    else await removeAdminMeaningPosition(selected.bookId, selected.meaningId, from)
    if (current !== generation) return
    target.value = null
    emit('changed')
  } catch (cause: unknown) {
    if (current !== generation) return
    const failure = getApiError(cause)
    if (failure.status === undefined) {
      ElMessage.warning('操作结果未知，正在重新读取位置；请核对后再决定是否重试。')
      target.value = null
      emit('unknown')
      return
    }
    if (failure.status === 404) {
      error.value = '源位置、教材或单元已不存在，请重新加载后核对。'
      reloadHint.value = true
    } else if (failure.status === 409) {
      error.value = '目标位置已存在；草稿已保留，请另选位置或重新加载。'
    } else {
      error.value = `操作失败：${failure.message}`
    }
  } finally {
    if (current === generation) saving.value = false
  }
}

function close() { target.value = null }
function changeMode() { if (mode.value === 'move' && !units.value.length) void loadUnits() }
</script>

<template>
  <el-dialog :model-value="target !== null" title="管理单元位置" width="min(520px, 96%)" @update:model-value="close">
    <template v-if="target">
      <p>教材：{{ target.bookName }} · {{ kindLabel(target.from.entryKind) }}：{{ target.word }} · {{ target.meaning }}</p>
      <p>当前位置：{{ unitLabel(target.from.unitId) }} · {{ sectionLabel(target.from.section) }} · {{ kindLabel(target.from.entryKind) }}</p>
      <el-radio-group v-model="mode" aria-label="位置操作" :disabled="saving" @change="changeMode">
        <el-radio-button value="move">调整位置</el-radio-button>
        <el-radio-button value="remove">从本单元移除</el-radio-button>
      </el-radio-group>
      <template v-if="mode === 'move'">
        <p>只调整这条位置；拼写与音标为跨教材共享字段。</p>
        <p v-if="loading">正在加载单元…</p>
        <p v-if="error && !units.length" role="alert">{{ error }} <el-button @click="loadUnits">重试单元列表</el-button></p>
        <el-form v-else label-position="top">
          <el-form-item label="目标单元">
            <el-select v-model="destination.unitId" aria-label="目标单元" :disabled="saving || loading">
              <el-option v-for="unit in units" :key="unit.id" :label="unitLabel(unit.id)" :value="unit.id" />
            </el-select>
          </el-form-item>
          <el-form-item label="目标分节">
            <el-select v-model="destination.section" aria-label="目标分节" :disabled="saving">
              <el-option label="未分节" :value="null" /><el-option label="Section A" value="A" /><el-option label="Section B" value="B" />
            </el-select>
          </el-form-item>
          <el-form-item label="目标类别">
            <el-select v-model="destination.entryKind" aria-label="目标类别" :disabled="saving">
              <el-option label="未分类" :value="null" /><el-option label="词条" value="word" /><el-option label="短语" value="phrase" />
            </el-select>
          </el-form-item>
        </el-form>
      </template>
      <p v-else>确认只移除此位置？其他类别、分节、单元位置和词义均保留。</p>
      <p v-if="error && (units.length || mode === 'remove')" role="alert">{{ error }} <el-button v-if="reloadHint" @click="emit('unknown'); target = null">重新加载</el-button></p>
    </template>
    <template #footer>
      <el-button :disabled="saving" @click="target = null">取消</el-button>
      <el-button :type="mode === 'remove' ? 'danger' : 'primary'" :loading="saving" :disabled="loading || (mode === 'move' && !units.some(unit => unit.id === destination.unitId))" @click="submit">
        {{ mode === 'remove' ? '确认只移除此位置' : '保存位置' }}
      </el-button>
    </template>
  </el-dialog>
</template>
