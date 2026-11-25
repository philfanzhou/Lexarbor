<script setup lang="ts">
import { CircleCheckFilled, CircleCloseFilled, InfoFilled, WarningFilled } from '@element-plus/icons-vue'
import type { NoticeTone } from '@/services/settingsStore'

// One line the page says about its own state: read, saved, refused or unknown.
defineProps<{ tone: NoticeTone; message: string }>()
const icons = { info: InfoFilled, success: CircleCheckFilled, warning: WarningFilled, error: CircleCloseFilled }
</script>

<template>
  <p class="lx-callout" :class="`lx-callout--${tone}`" role="status" aria-live="polite" aria-label="配置状态">
    <el-icon aria-hidden="true"><component :is="icons[tone]" /></el-icon>
    <span class="lx-callout__body">{{ message }}</span>
  </p>
</template>
