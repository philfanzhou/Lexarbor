<script setup lang="ts">
import { computed } from 'vue'
import type { SettingsPage } from '@/services/settingsStore'

// Stays at the bottom of the view while there is a draft, and says what saving will do.
const props = defineProps<{ page: SettingsPage }>()
const summary = computed(() => {
  const page = props.page
  if (page.busy.value) return '正在与服务器确认…'
  const parts = [`${page.changedFields.value.length} 处未保存`]
  if (page.liveCount.value) parts.push(`${page.liveCount.value} 项保存后立即生效`)
  if (page.restartCount.value) parts.push(`${page.restartCount.value} 项重启后生效`)
  if (!page.valid.value) parts.push(`${Object.keys(page.invalid.value).length} 项需要修正`)
  return parts.join('，')
})
</script>

<template>
  <div v-if="page.dirty.value || page.busy.value" class="lx-save-bar">
    <span class="lx-save-bar__text" role="status" aria-live="polite" aria-label="未保存的修改">{{ summary }}</span>
    <el-button :disabled="page.busy.value" @click="page.resetDraft()">放弃修改</el-button>
    <el-button
      type="primary"
      :disabled="page.busy.value || !page.valid.value"
      :loading="page.busy.value"
      @click="page.save()"
    >
      保存修改
    </el-button>
  </div>
</template>
