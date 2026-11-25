<script setup lang="ts">
import { computed } from 'vue'
import { keySegments } from '@/services/settingsLabels'

// A configuration key as it is shown beside its setting. A long key wraps, but only where one
// of its parts ends, never inside a word: "…EntityFrameworkCore.Database." then "Command".
const props = defineProps<{ value: string }>()
const segments = computed(() => keySegments(props.value))
const last = computed(() => segments.value.length - 1)
</script>

<template>
  <code><template v-for="(segment, index) in segments" :key="index">{{ segment }}<wbr v-if="index < last"></template></code>
</template>
