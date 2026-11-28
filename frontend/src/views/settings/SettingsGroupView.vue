<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { WarningFilled } from '@element-plus/icons-vue'
import RereadButton from '@/components/settings/RereadButton.vue'
import SettingRow from '@/components/settings/SettingRow.vue'
import SettingsFacts from '@/components/settings/SettingsFacts.vue'
import SettingsNotice from '@/components/settings/SettingsNotice.vue'
import SettingsSaveBar from '@/components/settings/SettingsSaveBar.vue'
import SettingsSection from '@/components/settings/SettingsSection.vue'
import SpeechSettingsPreview from '@/components/settings/SpeechSettingsPreview.vue'
import { useSettingsDraftGuard } from '@/router/settingsDraftGuard'
import { progressGroups, progressLink, reasonLabel, sectionNotes, sectionTitles, settingGroups } from '@/services/settingsLabels'
import type { SettingField, SettingValue } from '@/services/settingsCatalogApi'
import { useSettingsPage } from '@/services/settingsStore'

// One category of the settings surface. Every category is this page with another group: its
// rows come from the catalog, and each row's control follows the key's own kind.
const props = defineProps<{ groupId: string }>()
const route = useRoute()
const group = settingGroups.find(item => item.id === props.groupId)
const page = useSettingsPage(group?.id ?? '')
useSettingsDraftGuard(() => page.dirty.value && !page.busy.value)
onMounted(() => { void page.load() })

const value = (key: string) => page.field(key)?.value

// A speech style belongs to one protocol, which the server reads from the endpoint's host:
// MiniMax takes emotion, pitch and volume, and the OpenAI models that understand instructions
// take a style description.
const speechEndpoint = computed(() => String(page.draft['Models:Speech:Endpoint'] ?? ''))
const speechHost = computed(() => {
  try { return new URL(speechEndpoint.value).host } catch { return '' }
})
// An empty address speaks the way MiniMax does, which is how the server reads it.
const nativeSpeech = computed(() => !speechEndpoint.value.trim() || ['api.minimax.io', 'api.minimax.cn'].includes(speechHost.value))
const instructive = computed(() => speechHost.value === 'api.openai.com')
function shown(field: SettingField): boolean {
  if (props.groupId !== 'speech') return true
  if (field.key.endsWith(':Style')) return !nativeSpeech.value
  if (/(?:Emotion|Pitch|Volume)$/.test(field.key)) return nativeSpeech.value
  return true
}

const asides: Record<string, string> = {
  'audio-storage/connection': '保存后立即生效',
  'audio-storage/credentials': '保存后立即生效',
  'audio-storage/advanced': '保存后重启生效'
}
const sections = computed(() => {
  const order: string[] = []
  const bySection = new Map<string, SettingField[]>()
  for (const field of page.fields.value) {
    if (!shown(field)) continue
    if (!bySection.has(field.section)) {
      bySection.set(field.section, [])
      order.push(field.section)
    }
    bySection.get(field.section)!.push(field)
  }
  const path = `${props.groupId}/`
  return order.map(id => {
    const listed = bySection.get(id)!
    const fields = [...listed.filter(field => !derived(field)), ...listed.filter(derived)]
    // When the deployment owns every key of a section, its reason is said once for the section.
    const locked = fields.length > 0 && fields.every(field => field.apply === 'readOnly') ? fields[0].reason : null
    return {
      id,
      fields,
      lock: locked ? reasonLabel(locked) : null,
      title: sectionTitles[path + id] ?? page.catalog.value?.sections.find(item => item.group === props.groupId && item.id === id)?.name ?? id,
      note: sectionNotes[path + id] ?? null,
      aside: asides[path + id] ?? null
    }
  })
})
const derived = (field: SettingField) => field.apply === 'readOnly' && field.reason?.startsWith('Derived') === true

// The review assistant shares the reasoning model's connection; each accent also needs the speech one.
function reviewStatus(): string {
  if (value('MeaningSubmissions:Ai:AutoReviewEnabled') !== true) return '暂停：自动审核关闭，原人工审核和建议仍可用'
  if (!value('Models:Reasoning:Endpoint') || !value('Models:Reasoning:Model')) {
    return '暂停：推理模型的服务地址或模型缺失；配置修复后自动恢复，无需人工处理'
  }
  if (String(value('Models:Reasoning:Model')).length > 200) return '暂停：自动审核模型名称超过 200 字符'
  return '自动审核已启用；后台持续处理全部待审积压，失败后自动重试，无需人工处理'
}
const locales = { 'Models:Speech:Uk:Voice': 'uk', 'Models:Speech:Us:Voice': 'us' } as const
function localeStatus(locale: 'uk' | 'us'): string {
  const side = locale === 'uk' ? 'Uk' : 'Us'
  if (page.catalog.value?.facts.find(item => item.id === 'enrichmentConfigurationReady')?.value !== 'ready') return '等待：持久配置版本未就绪'
  if (!value('Models:Reasoning:Endpoint') || !value('Models:Reasoning:Model') || !page.field('Models:Reasoning:ApiKey')?.hasValue) {
    return '等待：推理模型的地址、模型或密钥缺失'
  }
  if (!value('Models:Speech:Endpoint') || !value('Models:Speech:Model') || !page.field('Models:Speech:ApiKey')?.hasValue
    || !value(`Models:Speech:${side}:Voice`) || value(`Models:Speech:${side}:Speed`) === null) {
    return `等待：语音服务或${locale === 'uk' ? '英式' : '美式'}音色、语速缺失`
  }
  return '本地必填配置齐全；供应商能力、存储及任务结果需另行核对'
}

// The draft preview works in the speech module's own names.
const model = computed(() => {
  const saved: Record<string, SettingValue> = {}
  const draft: Record<string, SettingValue> = {}
  const editable: Record<string, boolean> = {}
  for (const field of page.fields.value) {
    if (field.module !== 'enrichment' || !field.wire) continue
    saved[field.wire] = field.value
    draft[field.wire] = page.draft[field.key]
    editable[field.wire] = field.editable
  }
  return { saved, draft, editable }
})
const speechSecret = computed(() => page.secrets['Models:Speech:ApiKey'] ?? { action: 'keep' as const, value: '' })

// Arriving from the all-settings table: bring the row into view and point at it.
const focused = ref('')
let focusTimer: ReturnType<typeof setTimeout> | undefined
watch(() => page.status.value, async status => {
  const key = typeof route.query.focus === 'string' ? route.query.focus : ''
  if (status !== 'ready' || !key || !page.field(key)) return
  focused.value = key
  await nextTick()
  document.querySelector(`[data-setting="${CSS.escape(key)}"]`)?.scrollIntoView({ block: 'center' })
  clearTimeout(focusTimer)
  focusTimer = setTimeout(() => { focused.value = '' }, 2600)
}, { immediate: true })
</script>

<template>
  <div v-if="group" class="lx-page">
    <div class="lx-page__heading">
      <div>
        <h2 class="lx-page__title">{{ group.label }}</h2>
        <p class="lx-page__description">{{ group.description }}</p>
      </div>
      <RereadButton :page="page" />
    </div>

    <SettingsNotice :tone="page.tone.value" :message="page.notice.value" />

    <!-- The categories whose keys start background work lead on to where that work can be followed. -->
    <p v-if="progressGroups.includes(groupId)" class="lx-page__description">
      <RouterLink class="lx-inline-link lx-progress-link" :to="progressLink.to">{{ progressLink.label }}</RouterLink>
    </p>

    <div v-if="group.caution && page.status.value === 'ready'" class="lx-callout lx-callout--warning" role="note">
      <el-icon aria-hidden="true"><WarningFilled /></el-icon>
      <span class="lx-callout__body">{{ group.caution }}</span>
    </div>

    <template v-if="page.status.value === 'ready' && page.catalog.value">
      <SettingsSection
        v-for="section in sections"
        :key="section.id"
        :title="section.title"
        :note="section.note"
        :aside="section.aside"
        :lock="section.lock"
      >
        <SettingRow
          v-for="field in section.fields"
          :key="field.key"
          :field="field"
          :page="page"
          :focus="focused === field.key"
          :explain="!section.lock"
          @change="page.draft[$event.key] = $event.value"
          @secret="page.secrets[$event.key] = $event.value"
        >
          <template v-if="field.key === 'MeaningSubmissions:Ai:AutoReviewEnabled'" #after-row>
            <p class="lx-setting__meta" aria-label="自动审核配置状态">{{ reviewStatus() }}</p>
            <p class="lx-setting__meta lx-setting__meta--risk">
              开启后，AI 会直接通过、修改或驳回所有待审投稿，无需人工确认。错误结果可能进入公共词库，管理员承担此风险，可在词库中更正。
            </p>
          </template>
          <template v-else-if="field.key in locales" #after-row>
            <p class="lx-setting__meta" :aria-label="field.key === 'Models:Speech:Uk:Voice' ? 'UK 配置状态' : 'US 配置状态'">
              {{ localeStatus(locales[field.key as keyof typeof locales]) }}
            </p>
          </template>
        </SettingRow>
      </SettingsSection>

      <template v-if="groupId === 'speech'">
        <p class="lx-page__description">
          推理模型、语音模型、对应口音和音频存储都就绪后，系统自动生成缺失读音。
          持久配置版本：{{ page.catalog.value.facts.find(item => item.id === 'enrichmentConfigurationReady')?.value === 'ready' ? '已就绪' : '等待建立' }}。
        </p>
        <div v-if="!nativeSpeech && !instructive" class="lx-callout lx-callout--info" role="note">
          <el-icon aria-hidden="true"><WarningFilled /></el-icon>
          <span class="lx-callout__body">当前语音服务地址不是受支持的官方地址（api.minimax.io、api.minimax.cn 或 api.openai.com），生成读音时会安全停止。</span>
        </div>
        <section class="lx-card" aria-label="草稿试听">
          <div class="lx-card__head">
            <h3 class="lx-card__title">草稿试听</h3>
            <p class="lx-card__note">使用尚未保存的语音配置生成一段试听，不修改配置或词库。每次生成都会调用语音服务，可能产生费用。保存后用于后续生成，已有音频不会自动重新生成。</p>
          </div>
          <div class="lx-card__body">
            <SpeechSettingsPreview
              :revision="page.catalog.value.revision"
              :saved="model.saved"
              :draft="model.draft"
              :editable="model.editable"
              :credential="speechSecret"
              :disabled="page.busy.value"
            />
          </div>
        </section>
        <details class="lx-card lx-card--pad lx-details">
          <summary>语音能力限制</summary>
          <ul class="lx-details__list">
            <li>MiniMaxNative：仅支持 https://api.minimax.io/v1 或 https://api.minimax.cn/v1，模型 speech-2.8-hd / speech-2.8-turbo。要求覆盖完整朗读目标的 IPA，不接受自由指令；语速 0.5–2。</li>
            <li>OpenAICompatible：仅支持 https://api.openai.com/v1。gpt-4o-mini-tts / gpt-4o-mini-tts-2025-12-15 可用独立 instructions 表达口音及 IPA，语速 0.25–4；tts-1 / tts-1-hd 仅纯文本，准备结果含 IPA 或提示时会安全停止。</li>
            <li>兼容协议不代表任意模型支持指定读音。其他组合或无法表达的读音要求会安全停止，不会发出忽略约束的请求。音色需自行确认供应商支持与实际口音；本页不推荐也不预填音色、语速。</li>
            <li>配置允许保存 0.1–4 的语速，实际生成仍受上述更窄的范围限制。能力说明不能代替真实试听。<a href="https://github.com/philfanzhou/Lexarbor/blob/main/docs/api/Audio.md#speech-adapters" target="_blank" rel="noopener noreferrer">查看英文语音能力矩阵</a></li>
          </ul>
        </details>
      </template>

      <section v-if="groupId === 'service'" class="lx-card" aria-label="运行信息">
        <div class="lx-card__head">
          <h3 class="lx-card__title">运行信息</h3>
          <p class="lx-card__note">程序的固定属性和运行环境，不能通过配置修改。</p>
        </div>
        <div class="lx-card__body">
          <SettingsFacts :facts="page.catalog.value.facts" />
        </div>
      </section>

      <SettingsSaveBar :page="page" />
    </template>

    <div v-else-if="page.status.value === 'unknown'" class="lx-card lx-unknown">
      <el-icon aria-hidden="true"><WarningFilled /></el-icon>
      <h3>当前配置状态未知</h3>
      <p>读取失败，或服务器报告配置在别处被修改。在重新读取并核对之前，页面不会显示配置值，也不能保存{{ page.dirty.value ? '；你的草稿已保留' : '' }}。</p>
      <el-button type="primary" :loading="page.busy.value" @click="page.load()">重新读取</el-button>
    </div>
    <div v-else class="lx-card lx-card--pad" aria-busy="true" aria-label="正在读取配置">
      <el-skeleton :rows="8" animated />
    </div>
  </div>
</template>
