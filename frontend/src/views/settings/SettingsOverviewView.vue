<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ArrowRight, Refresh, Search, Timer, WarningFilled } from '@element-plus/icons-vue'
import RereadButton from '@/components/settings/RereadButton.vue'
import SettingBadge from '@/components/settings/SettingBadge.vue'
import SettingsFacts from '@/components/settings/SettingsFacts.vue'
import SettingsNotice from '@/components/settings/SettingsNotice.vue'
import { settingAreas, settingGroups } from '@/services/settingsLabels'
import { useSettingsPage } from '@/services/settingsStore'

// The landing page of the settings surface: what is configurable, what is waiting for a
// restart, and where to go next.
const router = useRouter()
const page = useSettingsPage(null)
const catalog = computed(() => page.catalog.value)
onMounted(() => { void page.load() })

interface Counts { total: number; live: number; restart: number; pending: number }
const empty = (): Counts => ({ total: 0, live: 0, restart: 0, pending: 0 })
const add = (counts: Counts, field: { apply: string; pendingRestart: boolean }) => {
  counts.total += 1
  if (field.apply === 'live') counts.live += 1
  if (field.apply === 'restart') counts.restart += 1
  if (field.pendingRestart) counts.pending += 1
}
const counts = computed(() => {
  const all = empty()
  const readOnly = { count: 0 }
  const byGroup: Record<string, Counts> = {}
  for (const field of catalog.value?.fields ?? []) {
    add(all, field)
    if (field.apply === 'readOnly') readOnly.count += 1
    add(byGroup[field.group] ??= empty(), field)
  }
  return { all, byGroup, readOnly: readOnly.count }
})
const areas = [...new Set(settingGroups.map(group => group.area))]
const inArea = (area: keyof typeof settingAreas) => settingGroups.filter(group => group.area === area)
const groupCount = (id: string) => counts.value.byGroup[id] ?? empty()
const areaCount = (area: keyof typeof settingAreas) => inArea(area).reduce((sum, group) => sum + groupCount(group.id).total, 0)
const fact = (id: string) => catalog.value?.facts.find(item => item.id === id)
const inContainer = computed(() => fact('container')?.value === 'true')

const query = ref('')
function search() {
  const q = query.value.trim()
  void router.push({ path: '/settings/all', query: q ? { q } : {} })
}
</script>

<template>
  <div class="lx-page">
    <div class="lx-page__heading">
      <div>
        <h2 class="lx-page__title">概览</h2>
        <p class="lx-page__description">全部配置的整体情况：哪些可以在这里修改，哪些在等待重启，哪些由部署管理。</p>
      </div>
      <RereadButton :page="page" />
    </div>

    <SettingsNotice :tone="page.tone.value" :message="page.notice.value" />

    <template v-if="page.status.value === 'ready' && catalog">
      <div v-if="counts.all.pending" class="lx-callout lx-callout--warning" role="status" aria-label="待重启的配置">
        <el-icon aria-hidden="true"><Timer /></el-icon>
        <span class="lx-callout__body">
          <strong class="lx-callout__title">{{ counts.all.pending }} 项配置已保存，重启服务后生效。</strong>
          当前进程仍在使用启动时的值。<template v-if="inContainer">重启容器：<code>docker restart lexarbor</code></template><template v-else>请重新启动 Lexarbor 进程</template>。
          <RouterLink class="lx-inline-link" to="/settings/all?filter=pending">查看待重启项</RouterLink>
        </span>
      </div>
      <div v-if="!catalog.storageAvailable" class="lx-callout lx-callout--error" role="alert">
        <el-icon aria-hidden="true"><WarningFilled /></el-icon>
        <span class="lx-callout__body">
          <strong class="lx-callout__title">配置文件当前无法读取或写入。</strong>
          在线保存已暂停，现有运行状态不受影响。请检查数据目录的权限后重新读取。
        </span>
      </div>
      <div v-else-if="catalog.externallyModified" class="lx-callout lx-callout--warning" role="status" aria-label="配置文件被外部修改">
        <el-icon aria-hidden="true"><WarningFilled /></el-icon>
        <span class="lx-callout__body">
          <strong class="lx-callout__title">配置文件在服务运行期间被外部修改。</strong>
          为保护这次修改，在线保存已暂停，重启后恢复；新值会在重启时生效。
        </span>
      </div>

      <div class="lx-tiles">
        <RouterLink class="lx-tile" to="/settings/all?filter=live" :aria-label="`立即生效：${counts.all.live} 项`">
          <span class="lx-tile__value">{{ counts.all.live }}</span>
          <span class="lx-tile__label"><SettingBadge tone="success">立即生效</SettingBadge><span>保存后马上起作用</span></span>
        </RouterLink>
        <RouterLink class="lx-tile" to="/settings/all?filter=restart" :aria-label="`重启生效：${counts.all.restart} 项`">
          <span class="lx-tile__value">{{ counts.all.restart }}</span>
          <span class="lx-tile__label"><SettingBadge tone="warning">重启生效</SettingBadge><span>下次启动时应用</span></span>
        </RouterLink>
        <RouterLink class="lx-tile" to="/settings/all?filter=readOnly" :aria-label="`只读：${counts.readOnly} 项`">
          <span class="lx-tile__value">{{ counts.readOnly }}</span>
          <span class="lx-tile__label"><SettingBadge tone="neutral">只读</SettingBadge><span>由部署管理</span></span>
        </RouterLink>
        <RouterLink class="lx-tile" to="/settings/all?filter=pending" :aria-label="`待重启：${counts.all.pending} 项`">
          <span class="lx-tile__value" :class="{ 'is-pending': counts.all.pending }">{{ counts.all.pending }}</span>
          <span class="lx-tile__label"><SettingBadge :tone="counts.all.pending ? 'warning' : 'neutral'">待重启</SettingBadge><span>已保存，等待重启</span></span>
        </RouterLink>
      </div>

      <form class="lx-card lx-card--pad" aria-label="搜索配置" @submit.prevent="search">
        <el-input
          v-model="query"
          class="lx-search"
          aria-label="搜索配置项"
          placeholder="搜索名称、配置键或说明，例如：限流、Audio、日志级别"
          clearable
          :suffix-icon="Search"
        >
          <template #append>
            <el-button type="primary" native-type="submit">搜索</el-button>
          </template>
        </el-input>
      </form>

      <section v-for="area in areas" :key="area" class="lx-card" :aria-label="settingAreas[area]">
        <div class="lx-card__head">
          <h2 class="lx-card__title">{{ settingAreas[area] }}</h2>
          <span class="lx-card__aside">{{ areaCount(area) }} 项</span>
        </div>
        <div class="lx-card__body lx-groups">
          <RouterLink
            v-for="group in inArea(area)"
            :key="group.id"
            class="lx-group-card"
            :to="group.path"
            :aria-label="`${group.label}：${groupCount(group.id).total} 项配置`"
          >
            <div class="lx-group-card__top">
              <span class="lx-group-card__icon" aria-hidden="true"><el-icon><component :is="group.icon" /></el-icon></span>
              <el-icon class="lx-group-card__arrow" aria-hidden="true"><ArrowRight /></el-icon>
            </div>
            <span class="lx-group-card__title">{{ group.label }}</span>
            <p class="lx-group-card__description">{{ group.description }}</p>
            <div class="lx-group-card__counts">
              <SettingBadge v-if="groupCount(group.id).live" tone="success">{{ groupCount(group.id).live }} 项立即生效</SettingBadge>
              <SettingBadge v-if="groupCount(group.id).restart" tone="warning">{{ groupCount(group.id).restart }} 项重启生效</SettingBadge>
              <SettingBadge v-if="groupCount(group.id).pending" tone="outline-warning">{{ groupCount(group.id).pending }} 项待重启</SettingBadge>
            </div>
          </RouterLink>
        </div>
      </section>

      <section class="lx-card" aria-label="运行信息">
        <div class="lx-card__head">
          <h2 class="lx-card__title">运行信息</h2>
          <p class="lx-card__note">程序的固定属性和运行环境，不能通过配置修改。</p>
        </div>
        <div class="lx-card__body">
          <SettingsFacts :facts="catalog.facts" />
        </div>
      </section>
    </template>

    <div v-else-if="page.status.value === 'unknown'" class="lx-card lx-unknown">
      <el-icon aria-hidden="true"><WarningFilled /></el-icon>
      <h3>当前配置状态未知</h3>
      <p>读取配置失败。这可能只是网络问题，也可能是配置文件无法访问；在确认之前，页面不会显示任何配置值。</p>
      <el-button :loading="page.busy.value" @click="page.load()"><el-icon v-if="!page.busy.value" aria-hidden="true"><Refresh /></el-icon>重新读取</el-button>
    </div>
    <div v-else class="lx-card lx-card--pad" aria-busy="true" aria-label="正在读取配置">
      <el-skeleton :rows="6" animated />
    </div>
  </div>
</template>
