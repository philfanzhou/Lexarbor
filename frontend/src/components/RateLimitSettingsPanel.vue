<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useSettingsDraftGuard } from '@/router/settingsDraftGuard'
import { sessionGeneration } from '@/services/authState'
import { getApiError } from '@/services/apiError'
import { readRateLimits, saveRateLimits } from '@/services/systemSettingsApi'
import type { RateLimitSettings } from '@/services/systemSettingsApi'

const lastRead = ref<RateLimitSettings | null>(null)
const available = ref(false)
const actual = computed(() => available.value ? lastRead.value : null)
const draft = ref(false)
const busy = ref(false)
const notice = ref('')
const generation = sessionGeneration.value
let saving = false
let active = true
let controller: AbortController | null = null
const current = () => active && generation === sessionGeneration.value
const sourceLabels = { default: '默认配置', file: '持久化文件', environment: '环境变量覆盖（网页只读）', commandLine: '启动参数覆盖（网页只读）' }
const changed = computed(() => {
  const settings = lastRead.value
  return settings !== null && settings.adminLogin.editable && settings.adminLogin.enabled !== draft.value
})
useSettingsDraftGuard(() => current() && !saving && changed.value)

function adopt(value: RateLimitSettings, resetDraft = false) {
  const previous = lastRead.value
  if (resetDraft || !previous || !value.adminLogin.editable || previous.adminLogin.enabled === draft.value) {
    draft.value = value.adminLogin.enabled
  }
  lastRead.value = value
  available.value = true
}

function begin() {
  controller?.abort()
  controller = new AbortController()
  busy.value = true
  return controller.signal
}
async function load() {
  if (!current() || busy.value) return
  const signal = begin()
  try {
    const value = await readRateLimits(signal, current)
    if (!current()) return
    adopt(value)
    notice.value = '已读取当前生效状态。'
  } catch {
    if (current()) {
      available.value = false
      notice.value = '状态未知：读取失败，请重试。'
    }
  } finally { if (current()) busy.value = false }
}
async function save() {
  if (!current() || busy.value || !actual.value || !changed.value) return
  const previous = actual.value
  const changes = { adminLoginEnabled: draft.value }
  const signal = begin()
  saving = true
  notice.value = '正在保存，生效状态等待确认。'
  let saved = false
  try {
    await saveRateLimits(previous.revision, changes, signal, current)
    saved = true
    if (!current()) return
    available.value = false
    const confirmed = await readRateLimits(signal, current)
    if (!current()) return
    adopt(confirmed, true)
    notice.value = '保存后已回读确认当前生效状态。'
  } catch (error: unknown) {
    if (!current()) return
    const status = getApiError(error).status
    if (saved || status === undefined) {
      available.value = false
      notice.value = '保存结果未知，正在回读确认；不会自动重复提交。'
      try {
        const recovered = await readRateLimits(signal, current)
        if (!current()) return
        adopt(recovered)
        notice.value = '保存响应未确认，已重新读取当前生效状态；草稿已保留，请核对。'
      } catch {
        if (current()) notice.value = '保存结果未知，回读失败；请重试读取当前状态。草稿已保留。'
      }
    } else {
      notice.value = status === 412
        ? '设置版本已变化，请重新读取后核对草稿再保存。'
        : status === 409 ? '字段被部署配置覆盖，请重新读取。草稿已保留。'
          : `保存失败（${status}）：${getApiError(error).message} 草稿已保留。`
      if (status === 412 || status === 409) available.value = false
    }
  } finally {
    saving = false
    if (current()) busy.value = false
  }
}
function cancel() {
  active = false
  controller?.abort()
  available.value = false
  lastRead.value = null
  draft.value = false
}
watch(sessionGeneration, cancel, { flush: 'sync' })
onBeforeUnmount(cancel)
onMounted(() => { void load() })
</script>

<template>
  <el-card class="rate-limit-settings" shadow="never">
    <section aria-labelledby="settings-heading">
      <h2 id="settings-heading">请求限流</h2>
      <p>登录发起请求按客户端地址计数。次数与窗口只读。关闭后重新开启会继续使用当前窗口。</p>
      <p role="status" aria-live="polite">{{ notice || '正在读取当前生效状态…' }}</p>
      <p v-if="!actual">当前生效状态：未知</p>
      <div class="policy-row">
        <h3>管理员登录</h3>
        <p>登录发起请求 /admin/auth/start</p>
        <template v-if="actual">
          <p>当前生效：<strong>{{ actual.adminLogin.enabled ? '开启' : '关闭' }}</strong></p>
          <p>{{ actual.adminLogin.permitLimit }} 次 / {{ actual.adminLogin.windowSeconds }} 秒 · {{ sourceLabels[actual.adminLogin.source] }}</p>
          <label>
            <input v-model="draft" type="checkbox" :disabled="busy || !actual.adminLogin.editable" aria-label="管理员登录草稿开关">
            草稿：{{ draft ? '开启' : '关闭' }}
          </label>
        </template>
      </div>
      <div class="settings-actions">
        <el-button type="primary" :disabled="!actual || !changed || busy" :loading="busy" @click="save">保存限流设置</el-button>
        <el-button :disabled="busy" @click="load">重新读取</el-button>
      </div>
    </section>
  </el-card>
</template>

<style scoped>
.rate-limit-settings h2 { margin-top: 0; }
.rate-limit-settings p { overflow-wrap: anywhere; line-height: 1.6; }
.policy-row { padding: var(--lx-space-3) 0; border-top: 1px solid var(--lx-color-border-light); }
.policy-row h3 { margin: 0; }
.policy-row input { margin-right: var(--lx-space-2); accent-color: var(--lx-color-primary); }
.settings-actions { display: flex; flex-wrap: wrap; gap: var(--lx-space-2); margin-top: var(--lx-space-4); }
.settings-actions .el-button { margin-left: 0; }
</style>
