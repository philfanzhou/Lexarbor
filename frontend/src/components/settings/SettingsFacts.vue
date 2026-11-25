<script setup lang="ts">
// The host's fixed properties and environment: reported, never configured.
import { computed } from 'vue'
import type { SettingFact } from '@/services/settingsCatalogApi'

const props = defineProps<{ facts: SettingFact[] }>()

const labels: Record<string, string> = {
  version: '版本', environment: '运行环境', container: '容器内运行', httpPort: 'HTTP 监听端口', serviceName: '服务名',
  configurationFile: '配置文件', administratorSignIn: '管理员登录', enrichmentConfigurationReady: '模型配置版本',
  startedAt: '进程启动时间（UTC）'
}
const channels: Record<string, string> = { release: '正式版', edge: '边缘版', development: '开发版' }
const words: Record<string, Record<string, string>> = {
  container: { true: '是', false: '否' },
  administratorSignIn: { configured: '已配置', 'not configured': '未配置' },
  enrichmentConfigurationReady: { ready: '已就绪', pending: '等待建立' },
  configurationFile: { Created: '已创建', 'Not created yet': '尚未创建，首次保存时创建' }
}

// The build identity arrives as one English sentence; the page says it in Chinese.
function detail(item: SettingFact): string | null {
  if (item.id === 'version') {
    const match = /^(\S+) channel, revision (\S+)$/.exec(item.detail ?? '')
    return match ? `渠道 ${channels[match[1]] ?? match[1]} · 提交 ${match[2] === 'unknown' ? '未知' : match[2]}` : item.detail
  }
  return item.id === 'httpPort' ? '固定，仅 HTTP/1' : null
}

const rows = computed(() => props.facts.filter(item => item.id in labels).map(item => ({
  id: item.id,
  label: labels[item.id],
  value: item.value === null ? '未知' : words[item.id]?.[item.value] ?? item.value,
  detail: detail(item)
})))
</script>

<template>
  <dl class="lx-facts">
    <div v-for="row in rows" :key="row.id">
      <dt>{{ row.label }}</dt>
      <dd>{{ row.value }}<span v-if="row.detail" class="lx-facts__detail"> · {{ row.detail }}</span></dd>
    </div>
  </dl>
</template>
