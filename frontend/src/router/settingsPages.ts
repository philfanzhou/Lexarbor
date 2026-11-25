import type { RouteRecordRaw } from 'vue-router'
import BusinessApiKeysPanel from '@/components/BusinessApiKeysPanel.vue'
import SettingsAllView from '@/views/settings/SettingsAllView.vue'
import SettingsGroupView from '@/views/settings/SettingsGroupView.vue'
import SettingsOverviewView from '@/views/settings/SettingsOverviewView.vue'
import { settingGroups } from '@/services/settingsLabels'

/**
 * The pages under /settings, children of the `settings` route. The group pages share one
 * component; the settings shell remounts it for each route, so every page starts without a draft.
 */
export const settingsPages = [
  { path: '', name: 'settings-overview', label: '概览', component: SettingsOverviewView },
  { path: 'all', name: 'settings-all', label: '全部配置', component: SettingsAllView },
  ...settingGroups.map(group => ({
    path: group.path.slice('/settings/'.length), name: `settings-${group.id}`, label: group.label,
    component: SettingsGroupView, props: { groupId: group.id }
  })),
  // The business API keys are kept in the database, not in configuration: the page stays beside
  // the configuration categories, outside the catalog.
  { path: 'api-keys', name: 'settings-api-keys', label: '接入 API Key', component: BusinessApiKeysPanel }
] satisfies (RouteRecordRaw & { label: string })[]

/** The full paths the hosted sign-in may return to; they mirror the backend's allowlist. */
export const settingsReturnRoutes: string[] = settingsPages.map(page => page.path ? `/settings/${page.path}` : '/settings')
