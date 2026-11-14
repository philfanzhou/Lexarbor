import type { RouteRecordRaw } from 'vue-router'
import BusinessApiKeysPanel from '@/components/BusinessApiKeysPanel.vue'
import RateLimitSettingsPanel from '@/components/RateLimitSettingsPanel.vue'
import AudioStorageSettingsPanel from '@/components/AudioStorageSettingsPanel.vue'
import EnrichmentSettingsPanel from '@/components/EnrichmentSettingsPanel.vue'

export const settingsPages = [
  { path: '/settings', name: 'settings-rate-limits', label: '请求限流', component: RateLimitSettingsPanel },
  { path: '/settings/api-keys', name: 'settings-api-keys', label: '接入 API Key', component: BusinessApiKeysPanel },
  { path: '/settings/audio-storage', name: 'settings-audio-storage', label: '音频存储', component: AudioStorageSettingsPanel },
  { path: '/settings/review', name: 'settings-review', label: '审核助手', component: EnrichmentSettingsPanel, props: { section: 'review' } },
  { path: '/settings/text', name: 'settings-text', label: '文本补齐', component: EnrichmentSettingsPanel, props: { section: 'text' } },
  { path: '/settings/speech', name: 'settings-speech', label: '语音生成', component: EnrichmentSettingsPanel, props: { section: 'speech' } },
  { path: '/settings/background', name: 'settings-background', label: '后台策略', component: EnrichmentSettingsPanel, props: { section: 'background' } }
] satisfies (RouteRecordRaw & { label: string })[]
