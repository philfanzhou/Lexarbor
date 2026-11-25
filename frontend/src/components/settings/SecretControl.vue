<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Key, Warning } from '@element-plus/icons-vue'
import type { SettingField } from '@/services/settingsCatalogApi'
import { fieldLabel } from '@/services/settingsLabels'
import type { SecretDraft } from '@/services/settingsStore'

// A credential the page never shows: presence, an explicit replacement, or an explicit clear.
// The value lives in this component's memory and in the page's draft, nowhere else.
const props = defineProps<{ field: SettingField; modelValue: SecretDraft; disabled: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: SecretDraft] }>()

const label = computed(() => fieldLabel(props.field))
const editing = ref(false)
const text = ref('')
const replacing = computed(() => props.modelValue.action === 'replace')

// The page resets every secret draft after a save attempt, a reread and a navigation.
watch(() => props.modelValue, value => {
  if (value.action === 'keep' && value.value === '') {
    editing.value = false
    text.value = ''
  }
})

function type(value: string) {
  text.value = value
  emit('update:modelValue', { action: value !== '' ? 'replace' : 'keep', value })
}
function keep() {
  editing.value = false
  text.value = ''
  emit('update:modelValue', { action: 'keep', value: '' })
}
function clear() {
  editing.value = false
  text.value = ''
  emit('update:modelValue', { action: 'clear', value: '' })
}
</script>

<template>
  <div class="lx-secret">
    <template v-if="editing || replacing">
      <el-input
        :model-value="text"
        type="password"
        show-password
        autocomplete="new-password"
        spellcheck="false"
        :maxlength="4096"
        :aria-label="label"
        :placeholder="`输入新的${label}`"
        :disabled="disabled"
        @update:model-value="type"
      />
      <div class="lx-secret__actions">
        <el-button link :disabled="disabled" @click="keep">取消</el-button>
      </div>
    </template>
    <template v-else-if="modelValue.action === 'clear'">
      <div class="lx-value is-unset-flag">
        <el-icon aria-hidden="true"><Warning /></el-icon>
        <span>保存后将清除</span>
      </div>
      <div class="lx-secret__actions">
        <el-button link :disabled="disabled" @click="keep">撤销</el-button>
      </div>
    </template>
    <template v-else>
      <div class="lx-value" :class="field.hasValue ? 'is-set' : 'is-empty'">
        <el-icon aria-hidden="true"><Key /></el-icon>
        <span>{{ field.hasValue ? '已设置（不显示原值）' : '未设置' }}</span>
      </div>
      <div class="lx-secret__actions">
        <el-button
          link
          type="primary"
          :disabled="disabled"
          :aria-label="`${label}：${field.hasValue ? '更换' : '设置'}`"
          @click="editing = true"
        >
          {{ field.hasValue ? '更换' : '设置' }}
        </el-button>
        <el-button
          v-if="field.hasValue"
          link
          type="danger"
          :disabled="disabled"
          :aria-label="`${label}：清除`"
          @click="clear"
        >
          清除
        </el-button>
      </div>
    </template>
  </div>
</template>
