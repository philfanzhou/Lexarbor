<script setup lang="ts">
import { computed, ref } from 'vue'
import { Close } from '@element-plus/icons-vue'
import type { SettingField } from '@/services/settingsCatalogApi'
import { fieldLabel, placeholder } from '@/services/settingsLabels'

// A short list of strings as tags: one entry per line of typing, committed on Enter or blur.
const props = defineProps<{ field: SettingField; modelValue: string[]; disabled: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: string[]] }>()

const label = computed(() => fieldLabel(props.field))
const pending = ref('')

function commit() {
  const entry = pending.value.trim()
  pending.value = ''
  // An empty or repeated entry is not added; the page reports what else is wrong.
  if (!entry || props.modelValue.includes(entry)) return
  emit('update:modelValue', [...props.modelValue, entry])
}
function keydown(event: KeyboardEvent) {
  if (event.key === 'Enter') {
    // Enter adds the entry itself; it must not submit anything else.
    event.preventDefault()
    commit()
  } else if (event.key === 'Backspace' && pending.value === '' && props.modelValue.length > 0) {
    emit('update:modelValue', props.modelValue.slice(0, -1))
  }
}
function remove(entry: string) {
  emit('update:modelValue', props.modelValue.filter(item => item !== entry))
}
</script>

<template>
  <div class="lx-list">
    <span v-for="entry in modelValue" :key="entry" class="lx-list__item">
      <span>{{ entry }}</span>
      <button
        type="button"
        class="lx-list__remove"
        :disabled="disabled"
        :aria-label="`移除 ${entry}`"
        @click="remove(entry)"
      >
        <el-icon aria-hidden="true"><Close /></el-icon>
      </button>
    </span>
    <input
      v-model="pending"
      class="lx-list__input"
      type="text"
      :aria-label="label"
      :placeholder="placeholder(field)"
      :inputmode="field.kind === 'integerList' ? 'numeric' : 'text'"
      :disabled="disabled"
      autocomplete="off"
      spellcheck="false"
      :maxlength="256"
      @keydown="keydown"
      @change="commit"
      @blur="commit"
    >
  </div>
</template>
