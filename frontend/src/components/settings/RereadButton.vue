<script setup lang="ts">
import { Refresh } from '@element-plus/icons-vue'
import type { SettingsPage } from '@/services/settingsStore'

// One reread on the page at a time: here while the state is known, in the card otherwise.
defineProps<{ page: SettingsPage }>()
</script>

<template>
  <div v-if="page.status.value === 'ready'" class="lx-page__actions">
    <el-button :loading="page.busy.value" @click="page.load()">
      <el-icon v-if="!page.busy.value" aria-hidden="true"><Refresh /></el-icon>重新读取
    </el-button>
  </div>
</template>
