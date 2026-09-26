<script setup lang="ts">
defineProps<{
  title: string
  description?: string
}>()
</script>

<template>
  <header class="page-header">
    <div class="page-header__text">
      <h1 class="page-header__title">{{ title }}</h1>
      <p v-if="description" class="page-header__description">{{ description }}</p>
    </div>
    <div v-if="$slots.actions" class="page-header__actions">
      <slot name="actions" />
    </div>
  </header>
</template>

<style scoped>
.page-header {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: var(--lx-space-4);
  margin-bottom: var(--lx-space-5);
}
.page-header__title {
  margin: 0;
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-xl);
  font-weight: 600;
  line-height: 1.3;
}
.page-header__description {
  margin: var(--lx-space-1) 0 0;
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-sm);
}
.page-header__actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--lx-space-3);
}

@media (max-width: 1023px) {
  .page-header {
    flex-direction: column;
    align-items: stretch;
    margin-bottom: var(--lx-space-4);
  }
}
</style>
