<script setup lang="ts">
// A small status label: 立即生效, 重启生效, 只读, 已修改, 待重启.
defineProps<{ tone?: 'neutral' | 'primary' | 'success' | 'warning' | 'danger' | 'info' | 'outline-warning' }>()
</script>

<template>
  <span class="lx-badge" :class="`lx-badge--${tone ?? 'neutral'}`"><slot /></span>
</template>
