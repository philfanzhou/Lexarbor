<script setup lang="ts">
// A boolean as a switch. It is its own element rather than a styled checkbox: the state is
// the aria-checked attribute, so assistive technology, tests and the page all read one value.
defineProps<{ modelValue: boolean; label: string; disabled?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
</script>

<template>
  <button
    type="button"
    role="switch"
    class="lx-switch"
    :class="{ 'is-on': modelValue }"
    :aria-checked="modelValue"
    :aria-label="label"
    :disabled="disabled"
    @click="emit('update:modelValue', !modelValue)"
  >
    <span class="lx-switch__track" aria-hidden="true"><span class="lx-switch__thumb" /></span>
  </button>
</template>
