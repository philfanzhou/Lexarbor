<script setup lang="ts">
// One configuration row: what the key is, where its value comes from, when a change takes
// effect, and the control or the read-only value.
import { computed } from 'vue'
import { Lock, Timer } from '@element-plus/icons-vue'
import type { SettingField, SettingValue } from '@/services/settingsCatalogApi'
import { fieldDescription, fieldLabel, formatValue, optionLabel, placeholder, reasonLabel, settingLabels } from '@/services/settingsLabels'
import type { SettingsPage, SecretDraft } from '@/services/settingsStore'
import ListControl from './ListControl.vue'
import SecretControl from './SecretControl.vue'
import SettingBadge from './SettingBadge.vue'
import SettingKey from './SettingKey.vue'
import SwitchControl from './SwitchControl.vue'

// `explain` is false inside a section that already says, once, why all of it is read-only.
const props = withDefaults(defineProps<{ field: SettingField; page: SettingsPage; focus?: boolean; explain?: boolean }>(), { explain: true })
// The row reads the page's draft and reports edits back; it never writes into the prop.
const emit = defineEmits<{
  change: [{ key: string; value: SettingValue }]
  secret: [{ key: string; value: SecretDraft }]
}>()

const label = computed(() => fieldLabel(props.field))
const description = computed(() => fieldDescription(props.field))
const draft = computed<SettingValue | undefined>(() => props.page.draft[props.field.key])
const changed = computed(() => props.page.changedFields.value.some(item => item.key === props.field.key))
const invalid = computed(() => props.page.invalid.value[props.field.key])
const editable = computed(() => props.field.editable && props.field.apply !== 'readOnly')
const disabled = computed(() => props.page.busy.value)
const secret = computed(() => props.page.secrets[props.field.key] ?? { action: 'keep' as const, value: '' })

const applyTone = computed(() => props.field.apply === 'live' ? 'success' : props.field.apply === 'restart' ? 'warning' : 'neutral')
const applyText = computed(() => props.field.apply === 'live' ? '立即生效' : props.field.apply === 'restart' ? '重启生效' : '只读')
// A key the deployment supplies is read-only here; its reason is the same for every such key.
const overridden = computed(() => !props.field.editable && props.field.apply !== 'readOnly')
const reason = computed(() => !editable.value && !overridden.value && props.explain && props.field.reason ? reasonLabel(props.field.reason) : null)

// A restart-only key runs with one value and will start with another. What the next start
// makes of a secret is said by its action or by what the page was told about it: a secret's
// saved form is never sent to the page.
const secretNext = computed<string | null>(() => {
  if (props.field.kind !== 'secret') return null
  const draft = secret.value
  if (draft.action === 'clear') return '清除'
  if (draft.action === 'replace' && draft.value.trim()) return '已更换（不显示原值）'
  return props.field.savedHasValue === null ? null : props.field.savedHasValue ? '已设置（不显示原值）' : '未设置'
})
const restarting = computed(() => props.field.apply === 'restart' && (props.field.pendingRestart || changed.value))
const nextStart = computed(() => props.field.kind === 'secret' ? secretNext.value : formatValue(props.field, draft.value))
const running = computed(() => formatValue(props.field, props.field.value))
const unset = computed(() => props.field.kind === 'secret' ? !props.field.hasValue : props.field.value === null || props.field.value === '')

const sourceText = { default: '默认值', file: '配置文件', environment: '环境变量', commandLine: '启动参数' } as const
const meta = computed(() => {
  const field = props.field
  // A model connection an upgrade left under an earlier release's section: said where the file keeps it.
  const location = field.location ? ` · 文件里仍是旧节名 ${field.location}` : ''
  if (!editable.value) {
    return `来源：${sourceText[field.source]}${overridden.value ? ' · 网页只读，请在部署配置中修改' : ''}${location}`
  }
  const parts = [`来源：${sourceText[field.source]}${location}`]
  // Only a number has a range to state. The whole number a key is read as is the ceiling of a key
  // with no smaller one: it is not a bound anyone chose, so it is not said.
  const ceiling = field.maximum === 2147483647 ? null : field.maximum
  if (number.value && field.minimum !== null && ceiling !== null) {
    parts.push(`范围 ${field.minimum}–${ceiling}`)
  } else if (number.value && (field.minimum !== null || ceiling !== null)) {
    parts.push(field.minimum !== null ? `不小于 ${field.minimum}` : `不大于 ${ceiling}`)
  }
  const fallback = field.defaultValue
  if (fallback !== null && fallback !== '' && !(Array.isArray(fallback) && fallback.length === 0)) {
    // A choice is named by its own value; its Chinese label is in the control.
    parts.push(`默认 ${field.kind === 'select' ? String(fallback) : formatValue(field, fallback)}`)
  }
  return parts.join(' · ')
})

function update(value: SettingValue) {
  emit('change', { key: props.field.key, value })
}
// An empty or unfinished number is null: the page says what is wrong before anything is sent.
function numberInput(text: string) {
  update(text === '' || !Number.isFinite(Number(text)) ? null : Number(text))
}
// Whole bounds around a whole default step by one; a fraction anywhere steps by a tenth.
const step = computed(() => {
  const field = props.field
  return field.kind === 'integer' || (Number.isInteger(field.minimum) && Number.isInteger(field.maximum)
    && !(typeof field.defaultValue === 'number' && !Number.isInteger(field.defaultValue))) ? '1' : '0.1'
})
// A style description is a sentence, so it gets a text area.
const multiline = computed(() => settingLabels[props.field.key]?.multiline === true)
const unit = computed(() => settingLabels[props.field.key]?.unit)
const number = computed(() => props.field.kind === 'integer' || props.field.kind === 'number')

// A tri-state key is three choices; null removes it so the next start reads the default.
const triState = computed({
  get: () => draft.value === null || draft.value === undefined ? 'auto' : draft.value ? 'yes' : 'no',
  set: (value: string) => update(value === 'auto' ? null : value === 'yes')
})
</script>

<template>
  <div
    class="lx-setting"
    :class="{ 'is-changed': changed, 'is-invalid': !!invalid, 'is-focus': focus }"
    :data-setting="field.key"
  >
    <div class="lx-setting__text">
      <div class="lx-setting__head">
        <span class="lx-setting__name">{{ label }}</span>
        <SettingBadge v-if="changed" tone="primary">已修改</SettingBadge>
        <SettingBadge v-else-if="field.pendingRestart" tone="outline-warning">
          <el-icon aria-hidden="true"><Timer /></el-icon>待重启
        </SettingBadge>
        <SettingBadge :tone="applyTone">
          <el-icon v-if="field.apply === 'readOnly'" aria-hidden="true"><Lock /></el-icon>{{ applyText }}
        </SettingBadge>
        <SettingKey :value="field.key" class="lx-setting__key" />
      </div>
      <p v-if="description" class="lx-setting__description">{{ description }}</p>
      <p v-if="reason" class="lx-setting__reason">
        <el-icon aria-hidden="true"><Lock /></el-icon>
        <span>{{ reason }}</span>
      </p>
    </div>

    <div class="lx-setting__control">
      <div v-if="!editable" class="lx-value" :class="unset ? 'is-empty' : ''">
        <el-icon aria-hidden="true"><Lock /></el-icon>
        <span>{{ running === '未设置' && field.raw ? field.raw : running }}</span>
      </div>
      <SecretControl
        v-else-if="field.kind === 'secret'"
        :field="field"
        :model-value="secret"
        :disabled="disabled"
        @update:model-value="$emit('secret', { key: field.key, value: $event })"
      />
      <div v-else-if="field.kind === 'boolean'" class="lx-setting__input">
        <SwitchControl :model-value="draft === true" :label="label" :disabled="disabled" @update:model-value="update($event)" />
        <span class="lx-setting__unit">{{ draft ? '开启' : '关闭' }}</span>
      </div>
      <select v-else-if="field.kind === 'triState'" v-model="triState" class="lx-select" :aria-label="label" :disabled="disabled">
        <option value="auto">自动（按运行环境判断）</option>
        <option value="yes">强制 HTTPS</option>
        <option value="no">不强制（仅在可信网络中使用）</option>
      </select>
      <select
        v-else-if="field.kind === 'select'"
        :value="String(draft ?? '')"
        class="lx-select"
        :aria-label="label"
        :disabled="disabled"
        @change="update(($event.target as HTMLSelectElement).value)"
      >
        <option v-for="option in field.options ?? []" :key="option" :value="option">{{ optionLabel(field, option) }}</option>
      </select>
      <ListControl
        v-else-if="field.kind === 'list' || field.kind === 'integerList'"
        :field="field"
        :model-value="Array.isArray(draft) ? (draft as string[]) : []"
        :disabled="disabled"
        @update:model-value="update($event)"
      />
      <div v-else-if="number" class="lx-setting__input">
        <!-- A plain number input: a value outside the range is refused and said, never silently clamped. -->
        <input
          class="lx-input"
          :class="{ 'is-invalid': invalid }"
          type="number"
          :value="draft ?? ''"
          :min="field.minimum ?? undefined"
          :max="field.maximum ?? undefined"
          :step="step"
          inputmode="decimal"
          :aria-label="label"
          :aria-invalid="invalid ? 'true' : undefined"
          :disabled="disabled"
          autocomplete="off"
          @input="numberInput(($event.target as HTMLInputElement).value)"
        >
        <span v-if="unit" class="lx-setting__unit">{{ unit }}</span>
      </div>
      <el-input
        v-else
        :model-value="String(draft ?? '')"
        :aria-label="label"
        :placeholder="placeholder(field)"
        :maxlength="multiline ? 512 : 2048"
        :type="multiline ? 'textarea' : 'text'"
        :rows="multiline ? 3 : undefined"
        :disabled="disabled"
        autocomplete="off"
        spellcheck="false"
        @update:model-value="update($event)"
      />

      <slot name="control-after" />
      <div v-if="restarting" class="lx-setting__pending" aria-label="重启后的值">
        <span class="lx-setting__pending-row"><span class="lx-setting__pending-label">当前生效</span><span>{{ running }}</span></span>
        <!-- Nothing is said of a next value the page cannot know, as for a secret it was told nothing about. -->
        <span v-if="nextStart !== null" class="lx-setting__pending-row">
          <span class="lx-setting__pending-label">{{ changed ? '保存并重启后' : '重启后' }}</span>
          <strong>{{ nextStart }}</strong>
        </span>
      </div>
      <p v-if="invalid" class="lx-setting__error" role="alert">{{ invalid }}</p>
      <p class="lx-setting__meta">{{ meta }}</p>
    </div>

    <!-- A note that belongs to the row as a whole, under both of its columns. -->
    <div v-if="$slots['after-row']" class="lx-setting__after"><slot name="after-row" /></div>
  </div>
</template>
