<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Search } from '@element-plus/icons-vue'
import RereadButton from '@/components/settings/RereadButton.vue'
import SettingBadge from '@/components/settings/SettingBadge.vue'
import SettingKey from '@/components/settings/SettingKey.vue'
import SettingsNotice from '@/components/settings/SettingsNotice.vue'
import { fieldDescription, fieldLabel, formatValue, settingAreas, settingGroups } from '@/services/settingsLabels'
import type { SettingApply, SettingField } from '@/services/settingsCatalogApi'
import { useSettingsPage } from '@/services/settingsStore'

// Every configuration key in one browsable list: searchable, filterable and paged. An entry
// leads to its group page, where it is edited. Three columns fit a tablet; a phone gets cards.
type Filter = 'all' | 'live' | 'restart' | 'readOnly' | 'pending' | 'overridden'
const filters: { id: Filter; label: string }[] = [
  { id: 'all', label: '全部' }, { id: 'live', label: '立即生效' }, { id: 'restart', label: '重启生效' },
  { id: 'pending', label: '待重启' }, { id: 'overridden', label: '被部署覆盖' }, { id: 'readOnly', label: '只读' }
]
const sourceText = { default: '默认值', file: '配置文件', environment: '环境变量', commandLine: '启动参数' } as const
const applyText = { live: '立即生效', restart: '重启生效', readOnly: '只读' } as const
const areas = [...new Set(settingGroups.map(item => item.area))]
const sizes = [10, 20, 50]

const route = useRoute()
const router = useRouter()
const page = useSettingsPage(null)
onMounted(() => { void page.load() })

const isFilter = (value: unknown): value is Filter => filters.some(item => item.id === value)
const queryOf = (value: unknown) => typeof value === 'string' ? value : ''
const filterOf = (value: unknown): Filter => isFilter(value) ? value : 'all'
const groupOf = (value: unknown) => settingGroups.some(item => item.id === value) ? String(value) : ''

const query = ref(queryOf(route.query.q))
const filter = ref<Filter>(filterOf(route.query.filter))
const group = ref(groupOf(route.query.group))
const pageNumber = ref(1)
const pageSize = ref(20)

// The URL keeps the view: the overview's links, a link followed while this page is already
// open, and the browser's history all work. Each side follows the other, and only when they differ.
watch(() => route.query, value => {
  if (query.value !== queryOf(value.q)) query.value = queryOf(value.q)
  if (filter.value !== filterOf(value.filter)) filter.value = filterOf(value.filter)
  if (group.value !== groupOf(value.group)) group.value = groupOf(value.group)
})
watch([query, filter, group], () => {
  pageNumber.value = 1
  const next = {
    ...(query.value.trim() ? { q: query.value.trim() } : {}),
    ...(filter.value !== 'all' ? { filter: filter.value } : {}),
    ...(group.value ? { group: group.value } : {})
  }
  const inUrl = (key: 'q' | 'filter' | 'group') => next[key] === undefined
    ? route.query[key] === undefined : route.query[key] === next[key]
  if ((['q', 'filter', 'group'] as const).every(inUrl)) return
  void router.replace({ query: next })
})

// Cards below the phone breakpoint, where even three columns would not fit.
const narrow = '(max-width: 767px)'
const cards = ref(window.matchMedia(narrow).matches)
onMounted(() => {
  const media = window.matchMedia(narrow)
  const follow = () => { cards.value = media.matches }
  media.addEventListener('change', follow)
  onBeforeUnmount(() => media.removeEventListener('change', follow))
})

const inGroup = (item: SettingField) => !group.value || item.group === group.value
function passes(id: Filter, item: SettingField): boolean {
  switch (id) {
    case 'live': return item.apply === 'live'
    case 'restart': return item.apply === 'restart'
    case 'readOnly': return item.apply === 'readOnly'
    case 'pending': return item.pendingRestart
    case 'overridden': return item.apply !== 'readOnly' && !item.editable
    default: return true
  }
}
const searched = computed(() => {
  const terms = query.value.trim().toLowerCase().split(/\s+/).filter(Boolean)
  return (page.catalog.value?.fields ?? []).filter(item => {
    if (!inGroup(item)) return false
    const haystack = [fieldLabel(item), item.name, item.key, fieldDescription(item) ?? '', item.description ?? '']
      .join('\n').toLowerCase()
    return terms.every(term => haystack.includes(term))
  })
})
const rows = computed(() => searched.value.filter(item => passes(filter.value, item)))
const pages = computed(() => Math.max(1, Math.ceil(rows.value.length / pageSize.value)))
const visible = computed(() => rows.value.slice((pageNumber.value - 1) * pageSize.value, pageNumber.value * pageSize.value))
const count = (id: Filter) => searched.value.filter(item => passes(id, item)).length
const groupLabel = (id: string) => settingGroups.find(item => item.id === id)?.label ?? id
const target = (item: SettingField) => ({ path: `/settings/${item.group}`, query: { focus: item.key } })
const waiting = (item: SettingField) => item.pendingRestart && item.apply !== 'readOnly'
function clear() {
  query.value = ''
  filter.value = 'all'
  group.value = ''
}
</script>

<template>
  <div class="lx-page">
    <div class="lx-page__heading">
      <div>
        <h2 class="lx-page__title">全部配置</h2>
        <p class="lx-page__description">{{ page.catalog.value?.fields.length ?? 0 }} 个配置项。点击任一项进入所属分组修改。</p>
      </div>
      <RereadButton :page="page" />
    </div>

    <SettingsNotice :tone="page.tone.value" :message="page.notice.value" />

    <template v-if="page.status.value === 'ready' && page.catalog.value">
      <div class="lx-toolbar">
        <div class="lx-toolbar__row">
          <el-input v-model="query" class="lx-toolbar__search" aria-label="搜索配置项" placeholder="搜索名称、配置键或说明" clearable :suffix-icon="Search" />
          <select v-model="group" class="lx-select lx-select--inline" aria-label="按分组筛选">
            <option value="">全部分组</option>
            <optgroup v-for="area in areas" :key="area" :label="settingAreas[area]">
              <option v-for="item in settingGroups.filter(entry => entry.area === area)" :key="item.id" :value="item.id">{{ item.label }}</option>
            </optgroup>
          </select>
        </div>
        <div class="lx-chips" aria-label="按生效方式筛选">
          <button
            v-for="item in filters"
            :key="item.id"
            type="button"
            class="lx-chip"
            :aria-pressed="filter === item.id"
            :aria-label="`${item.label}：${count(item.id)} 项`"
            @click="filter = item.id"
          >
            {{ item.label }}<span class="lx-chip__count">{{ count(item.id) }}</span>
          </button>
        </div>
      </div>

      <section class="lx-card" aria-label="配置列表">
        <el-table v-if="visible.length && !cards" :data="visible" class="lx-table" row-key="key" scrollbar-tabindex="0" @row-click="router.push(target($event))">
          <el-table-column label="配置项" min-width="300">
            <template #default="{ row }">
              <!-- The entry's name and its category share a line; the key takes the next, however long it is. -->
              <RouterLink class="lx-table__name" :to="target(row)" :title="row.editable ? '打开并修改' : '打开查看'">{{ fieldLabel(row) }}</RouterLink>
              <span v-if="!group" class="lx-table__group">{{ groupLabel(row.group) }}</span>
              <SettingKey :value="row.key" class="lx-table__key" />
            </template>
          </el-table-column>
          <el-table-column label="当前生效值" min-width="210">
            <template #default="{ row }">
              <span class="lx-table__value">{{ formatValue(row, row.value) }}</span>
              <span v-if="row.pendingRestart" class="lx-table__next">重启后为 {{ formatValue(row, row.savedValue) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="来源与生效方式" width="168">
            <template #default="{ row }">
              <div class="lx-table__badges">
                <SettingBadge v-if="waiting(row)" tone="outline-warning">待重启</SettingBadge>
                <SettingBadge v-else :tone="row.apply === 'live' ? 'success' : row.apply === 'restart' ? 'warning' : 'neutral'">{{ applyText[row.apply as SettingApply] }}</SettingBadge>
                <SettingBadge tone="neutral">{{ sourceText[row.source as keyof typeof sourceText] }}</SettingBadge>
              </div>
            </template>
          </el-table-column>
        </el-table>

        <ul v-else-if="visible.length" class="lx-rows">
          <li v-for="row in visible" :key="row.key">
            <!-- A card says everything it holds; its name stays the entry's own. -->
            <RouterLink class="lx-row" :to="target(row)" :aria-label="`打开 ${fieldLabel(row)}`">
              <span class="lx-row__head">
                <span class="lx-table__name">{{ fieldLabel(row) }}</span>
                <SettingBadge v-if="waiting(row)" tone="outline-warning">待重启</SettingBadge>
                <SettingBadge v-else :tone="row.apply === 'live' ? 'success' : row.apply === 'restart' ? 'warning' : 'neutral'">{{ applyText[row.apply as SettingApply] }}</SettingBadge>
              </span>
              <SettingKey :value="row.key" class="lx-table__key" />
              <span class="lx-table__value">{{ formatValue(row, row.value) }}</span>
              <span v-if="row.pendingRestart" class="lx-table__next">重启后为 {{ formatValue(row, row.savedValue) }}</span>
              <span class="lx-setting__meta">{{ groupLabel(row.group) }} · 来源：{{ sourceText[row.source as keyof typeof sourceText] }}</span>
            </RouterLink>
          </li>
        </ul>

        <div v-else class="lx-card__body">
          <el-empty :description="page.catalog.value.fields.length ? '没有符合条件的配置项' : '服务器没有返回任何配置项'">
            <el-button v-if="page.catalog.value.fields.length" @click="clear">清除搜索和筛选</el-button>
          </el-empty>
        </div>

        <div v-if="rows.length" class="lx-pagination">
          <label class="lx-pagination__size">
            每页条数
            <select v-model.number="pageSize" class="lx-select lx-select--inline" aria-label="每页条数">
              <option v-for="size in sizes" :key="size" :value="size">{{ size }}</option>
            </select>
          </label>
          <span class="lx-setting__meta">共 {{ rows.length }} 项 · 第 {{ pageNumber }} / {{ pages }} 页</span>
          <el-pagination v-model:current-page="pageNumber" :page-size="pageSize" :total="rows.length" layout="prev, pager, next" />
        </div>
      </section>

      <details v-if="page.catalog.value.unknownFileKeys.length" class="lx-card lx-card--pad lx-details">
        <summary>
          配置文件中还有 {{ page.catalog.value.unknownFileKeys.length }} 个 Lexarbor 不读取的键
          <span class="lx-setting__meta">它们来自手工编辑或旧版本，可以保留，也可以停机后从文件中删除。</span>
        </summary>
        <ul class="lx-unknown-keys">
          <li v-for="key in page.catalog.value.unknownFileKeys" :key="key"><code>{{ key }}</code></li>
        </ul>
      </details>
    </template>

    <div v-else-if="page.status.value === 'unknown'" class="lx-card lx-unknown">
      <h3>当前配置状态未知</h3>
      <p>读取配置失败。在确认之前，页面不会显示任何配置值。</p>
      <el-button :loading="page.busy.value" @click="page.load()">重新读取</el-button>
    </div>
    <div v-else class="lx-card lx-card--pad" aria-busy="true" aria-label="正在读取配置">
      <el-skeleton :rows="8" animated />
    </div>
  </div>
</template>
