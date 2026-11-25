<script setup lang="ts">
import { Lock } from '@element-plus/icons-vue'
// A titled card that holds the configuration rows of one section. A section whose every
// key is read-only says why once, here, instead of under each row.
defineProps<{ title: string; note?: string | null; aside?: string | null; lock?: string | null }>()
</script>

<template>
  <section class="lx-card">
    <div class="lx-card__head">
      <h3 class="lx-card__title">{{ title }}</h3>
      <span v-if="aside" class="lx-card__aside">{{ aside }}</span>
      <p v-if="note" class="lx-card__note">{{ note }}</p>
    </div>
    <p v-if="lock" class="lx-card__lock" role="note">
      <el-icon aria-hidden="true"><Lock /></el-icon>
      <span><strong>本节的配置由部署管理，网页只读。</strong>{{ lock }}</span>
    </p>
    <slot />
  </section>
</template>
