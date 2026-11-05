<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import { settingsPages } from '@/router/settingsPages'
</script>

<template>
  <PageHeader title="系统设置" />
  <nav class="settings-tabs" aria-label="设置分类">
    <RouterLink v-for="page in settingsPages" :key="page.name" :to="{ name: page.name }">
      {{ page.label }}
    </RouterLink>
  </nav>
  <RouterView v-slot="{ Component, route }">
    <component :is="Component" :key="route.name" />
  </RouterView>
</template>

<style scoped>
.settings-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lx-space-2);
  margin-bottom: var(--lx-space-4);
}

.settings-tabs a {
  display: inline-flex;
  align-items: center;
  min-height: 44px;
  padding: 0 var(--lx-space-3);
  border: 1px solid var(--lx-color-border);
  border-radius: var(--lx-radius-sm);
  background: var(--lx-color-bg-surface);
  color: var(--lx-color-text-regular);
  text-decoration: none;
}

.settings-tabs a:hover,
.settings-tabs a[aria-current='page'] {
  border-color: var(--lx-color-primary);
  background: var(--lx-color-primary-soft);
  color: var(--lx-color-primary);
}

.settings-tabs a[aria-current='page'] {
  font-weight: 600;
}

.settings-tabs a:focus-visible {
  outline: 2px solid var(--lx-color-focus);
  outline-offset: 2px;
}
</style>
