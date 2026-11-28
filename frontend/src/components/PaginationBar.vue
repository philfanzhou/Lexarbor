<script setup lang="ts">
// The page-size picker beside the pager. Both write the page's own `page` and `size`: picking a
// size restarts from the first page, and every change is announced so the page loads again.
const page = defineModel<number>('page', { required: true })
const size = defineModel<number>('size', { required: true })
defineProps<{ total: number }>()
const emit = defineEmits<{ change: [] }>()
const pageSizes = [10, 20, 50, 100]

function sizeChanged(value: number) {
  size.value = value
  page.value = 1
  emit('change')
}

function pageChanged(value: number) {
  page.value = value
  emit('change')
}
</script>

<template>
  <div class="pagination-wrapper">
    <el-select
      v-model="size"
      class="page-size"
      aria-label="每页条数"
      @change="sizeChanged"
    >
      <el-option
        v-for="option in pageSizes"
        :key="option"
        :label="`${option} 条/页`"
        :value="option"
      />
    </el-select>
    <el-pagination
      v-model:current-page="page"
      v-model:page-size="size"
      :total="total"
      layout="total, prev, pager, next, jumper"
      background
      @size-change="sizeChanged"
      @current-change="pageChanged"
    />
  </div>
</template>

<style scoped>
.pagination-wrapper {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: var(--lx-space-3);
  margin-top: var(--lx-space-4);
}
.page-size {
  width: 120px;
}
</style>
