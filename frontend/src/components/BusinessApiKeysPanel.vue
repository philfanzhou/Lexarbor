<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import api from '@/services/api'
import { getApiError } from '@/services/apiError'
import { sessionGeneration } from '@/services/authState'

interface KeyMetadata {
  id: string
  callerId: string
  scopes: string[]
  createdAtUnixMs: number
  revokedAtUnixMs: number | null
}

const path = '/admin/api-keys'
const items = ref<KeyMetadata[]>([])
const callerId = ref('')
const catalog = ref(false)
const meaningSubmissions = ref(false)
const issuedKey = ref('')
const busy = ref(false)
const notice = ref('')
const generation = sessionGeneration.value
const controller = new AbortController()
const isCurrent = () => !controller.signal.aborted && generation === sessionGeneration.value
const config = { signal: controller.signal, lxIsCurrent: isCurrent }
const scopeLabels: Record<string, string> = { catalog: '词汇目录与出题', 'meaning-submissions': '释义投稿' }
const time = (unixMs: number) => new Date(unixMs).toLocaleString()

async function refresh() {
  const data = await api.get<{ items: KeyMetadata[] }>(path, config)
  if (isCurrent()) items.value = data.items
}

async function run(action: () => Promise<void>, failure: string) {
  if (busy.value || !isCurrent()) return
  busy.value = true
  notice.value = ''
  try {
    await action()
  } catch (error) {
    if (isCurrent()) notice.value = `${failure} ${getApiError(error).message}`
  } finally {
    if (isCurrent()) busy.value = false
  }
}

async function issue() {
  if (issuedKey.value || !callerId.value.trim() || (!catalog.value && !meaningSubmissions.value)) return
  const scopes: string[] = []
  if (catalog.value) scopes.push('catalog')
  if (meaningSubmissions.value) scopes.push('meaning-submissions')
  await run(async () => {
    const data = await api.post<{ key: string; metadata: KeyMetadata }>(path, { callerId: callerId.value.trim(), scopes }, config)
    if (!isCurrent()) return
    items.value = [data.metadata, ...items.value]
    issuedKey.value = data.key
    notice.value = '已签发。请将 Key 保存到接入服务的后端配置。'
  }, '签发结果未确认，请重新读取列表后核对。')
}

async function revoke(item: KeyMetadata) {
  if (!window.confirm(`撤销「${item.callerId}」的这个 Key？使用该 Key 的请求将被拒绝。`)) return
  await run(async () => {
    await api.delete(`${path}/${encodeURIComponent(item.id)}`, config)
    await refresh()
    if (isCurrent()) notice.value = '已撤销。'
  }, '撤销结果未确认，请重新读取列表后核对。')
}

function clear() {
  controller.abort()
  issuedKey.value = ''
  callerId.value = ''
  items.value = []
}
watch(sessionGeneration, clear, { flush: 'sync' })
onBeforeUnmount(clear)
onMounted(() => { void run(refresh, '无法读取 API Key 列表。') })
</script>

<template>
  <el-card shadow="never" class="api-keys-panel">
    <section aria-labelledby="api-keys-heading">
      <h2 id="api-keys-heading">接入 API Key</h2>
      <form class="key-form" @submit.prevent="issue">
        <label for="api-key-caller">调用方标识</label>
        <el-input id="api-key-caller" v-model="callerId" :disabled="busy" :maxlength="2048" autocomplete="off" />
        <fieldset>
          <legend>权限</legend>
          <label><input v-model="catalog" type="checkbox" :disabled="busy"> 词汇目录与出题</label>
          <label><input v-model="meaningSubmissions" type="checkbox" :disabled="busy"> 释义投稿</label>
        </fieldset>
        <p>轮换时使用相同标识签发新 Key，接入方切换后再撤销旧 Key。</p>
        <el-button type="primary" native-type="submit" :disabled="busy || !!issuedKey || !callerId.trim() || (!catalog && !meaningSubmissions)">签发 Key</el-button>
      </form>
      <p role="status" aria-live="polite" aria-label="API Key 状态">{{ notice }}</p>
      <div v-if="issuedKey" class="one-time-key">
        <label for="api-key-secret">Key 明文，仅此次显示</label>
        <el-input id="api-key-secret" :model-value="issuedKey" readonly autocomplete="off" />
        <el-button @click="issuedKey = ''">关闭明文</el-button>
      </div>
      <el-button :disabled="busy" @click="run(refresh, '无法读取 API Key 列表。')">重新读取</el-button>
      <el-table :data="items" row-key="id" empty-text="暂无 API Key" class="key-list">
        <el-table-column prop="callerId" label="调用方标识" min-width="150" />
        <el-table-column label="权限" min-width="145">
          <template #default="{ row }">{{ row.scopes.map((scope: string) => scopeLabels[scope] ?? scope).join('、') }}</template>
        </el-table-column>
        <el-table-column label="签发时间" min-width="145">
          <template #default="{ row }">{{ time(row.createdAtUnixMs) }}</template>
        </el-table-column>
        <el-table-column label="状态" min-width="155">
          <template #default="{ row }">{{ row.revokedAtUnixMs === null ? '有效' : `已撤销 · ${time(row.revokedAtUnixMs)}` }}</template>
        </el-table-column>
        <el-table-column label="操作" width="100">
          <template #default="{ row }">
            <el-button v-if="row.revokedAtUnixMs === null" type="danger" plain :disabled="busy" @click="revoke(row)">撤销</el-button>
          </template>
        </el-table-column>
      </el-table>
    </section>
  </el-card>
</template>

<style scoped>
.api-keys-panel h2 { margin-top: 0; }
.key-form, .one-time-key { display: grid; gap: var(--lx-space-2); max-width: 40rem; }
.key-form p { margin: 0; }
.key-form fieldset { display: flex; flex-wrap: wrap; gap: var(--lx-space-3); border: 1px solid var(--lx-color-border-light); }
.key-form .el-button, .one-time-key .el-button { justify-self: start; }
.one-time-key, .key-list { margin-bottom: var(--lx-space-3); }
.key-list { margin-top: var(--lx-space-3); }
</style>
