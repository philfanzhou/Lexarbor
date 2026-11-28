<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '@/components/PageHeader.vue'
import SettingBadge from '@/components/settings/SettingBadge.vue'
import { settingsCatalog } from '@/services/settingsStore'
import { apiKeysGroup, settingAreas, settingGroups, type SettingGroupLabel } from '@/services/settingsLabels'

const browse = [
  { path: '/settings', label: '概览' },
  { path: '/settings/all', label: '全部配置' }
]
const areas = [...new Set(settingGroups.map(group => group.area))]
// The API keys are managed in the database, not configured: their page sits beside the
// categories, right after the rate limits.
type Link = Pick<SettingGroupLabel, 'id' | 'path' | 'label' | 'icon'>
const linksOf = (area: keyof typeof settingAreas): Link[] => settingGroups.filter(group => group.area === area)
  .flatMap(group => area === 'access' && group.id === 'rate-limits' ? [group, apiKeysGroup] : [group])
const pendingIn = (group: string) => (settingsCatalog.value?.fields ?? []).filter(field => field.group === group && field.pendingRestart).length

// On a phone the categories are one scrolling strip. It keeps the current category in view, and
// fades at its end only while categories remain beyond it.
const current = useRoute()
const strip = ref<HTMLElement | null>(null)
const more = ref(false)
function track() {
  const element = strip.value
  if (element) more.value = element.scrollWidth > element.clientWidth + 1 && element.scrollLeft + element.clientWidth < element.scrollWidth - 1
}
// Horizontal only: the page itself is never scrolled to the strip.
function center() {
  const element = strip.value
  if (!element) return
  const current = element.querySelector<HTMLElement>('[aria-current="page"]')
  if (current && element.scrollWidth > element.clientWidth) element.scrollLeft = current.offsetLeft - (element.clientWidth - current.offsetWidth) / 2
  track()
}
onMounted(() => {
  center()
  window.addEventListener('resize', center)
})
onBeforeUnmount(() => window.removeEventListener('resize', center))
watch(() => current.fullPath, () => { void nextTick(center) })
</script>

<template>
  <PageHeader title="系统设置" description="查看和管理 Lexarbor 的全部配置。带「立即生效」的保存后马上起作用；带「重启生效」的写入配置文件，下次启动时应用。">
    <template #actions>
      <div class="lx-legend" aria-label="生效方式说明">
        <SettingBadge tone="success">立即生效</SettingBadge>
        <SettingBadge tone="warning">重启生效</SettingBadge>
        <SettingBadge tone="neutral">只读</SettingBadge>
      </div>
    </template>
  </PageHeader>
  <div class="lx-settings">
    <nav class="lx-settings__nav" aria-label="设置分类">
      <ul class="lx-settings__links">
        <li v-for="page in browse" :key="page.path">
          <RouterLink :to="page.path" class="lx-settings__link">
            <span class="lx-settings__link-text">{{ page.label }}</span>
          </RouterLink>
        </li>
      </ul>
      <div ref="strip" class="lx-settings__groups" :class="{ 'has-more': more }" @scroll.passive="track">
        <template v-for="area in areas" :key="area">
          <span class="lx-settings__area">{{ settingAreas[area] }}</span>
          <ul class="lx-settings__links">
            <li v-for="link in linksOf(area)" :key="link.id">
              <RouterLink
                :to="link.path"
                class="lx-settings__link"
                :title="pendingIn(link.id) ? `${link.label}：${pendingIn(link.id)} 项已保存，等待重启生效` : undefined"
              >
                <el-icon class="lx-settings__icon" aria-hidden="true"><component :is="link.icon" /></el-icon>
                <span class="lx-settings__link-text">{{ link.label }}</span>
                <span v-if="pendingIn(link.id)" class="lx-settings__pending" aria-hidden="true" />
              </RouterLink>
            </li>
          </ul>
        </template>
      </div>
    </nav>
    <div>
      <RouterView v-slot="{ Component, route }">
        <component :is="Component" :key="route.name" />
      </RouterView>
    </div>
  </div>
</template>
