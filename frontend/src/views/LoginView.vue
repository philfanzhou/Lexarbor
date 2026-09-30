<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import type { FormInstance, FormRules } from 'element-plus'
import { authMethod, loadAuthMethod, login } from '@/services/authState'
import type { AuthMethod } from '@/services/authApi'
import { getApiError } from '@/services/apiError'

const route = useRoute()
const router = useRouter()
const formRef = ref<FormInstance>()
const loading = ref(false)
const form = ref({
  username: '',
  password: ''
})

// The mode decides which single sign-in action the card offers. While it is
// unknown — still loading, or the read failed — no submittable password form
// and no hosted navigation is rendered at all, so neither can flash or be
// used before the deployment's real mode is known. A cached mode (the logout
// of the same page load prefetched it) renders immediately without a refetch.
const mode = ref<AuthMethod | null>(authMethod.value)
const modeLoading = ref(mode.value === null)
const modeFailed = ref(false)
const starting = ref(false)

onMounted(() => {
  if (mode.value === null) {
    void fetchMode()
  }
})

async function fetchMode() {
  modeLoading.value = true
  modeFailed.value = false
  try {
    mode.value = await loadAuthMethod()
  } catch {
    mode.value = null
    modeFailed.value = true
  } finally {
    modeLoading.value = false
  }
}

/**
 * The six fixed reasons of the hosted callback (`canceled`, `denied`,
 * `sign_in_failed`, `provider_unavailable`) and of the prepared-logout return
 * route (`logged_out`, `logout_failed`). Each has exactly one message; an
 * unknown value stays harmless and shows nothing.
 */
const reasonNotices: Record<string, { text: string; type: 'success' | 'info' | 'warning' | 'error' }> = {
  canceled: { text: '已取消登录，可重新发起。', type: 'info' },
  denied: { text: '当前账户没有管理员权限。', type: 'error' },
  sign_in_failed: { text: '登录失败，请重试。', type: 'error' },
  provider_unavailable: { text: '身份提供方暂时不可用，请稍后重试。', type: 'error' },
  logged_out: { text: '已退出登录。', type: 'success' },
  logout_failed: {
    text: 'Lexarbor 已退出，但身份提供方的登出未确认完成，它可能仍保留会话。',
    type: 'warning'
  }
}

const reasonNotice = computed(() => {
  const reason = route.query.reason
  return typeof reason === 'string' ? reasonNotices[reason] ?? null : null
})

const rules: FormRules = {
  username: [{ required: true, message: '请输入用户名', trigger: 'blur' }],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }]
}

function getRedirectTarget() {
  const redirect = route.query.redirect
  return typeof redirect === 'string' && redirect.startsWith('/') && redirect !== '/login'
    ? redirect
    : '/books'
}

// The exact route shapes the start route's allowlist accepts, mirrored here so
// an unusable `redirect` is dropped — the backend then falls back to its own
// default — instead of turning a top-level navigation into a bare 400.
const hostedReturnRoutes = new Set([
  '/books',
  '/vocabulary',
  '/phrases',
  '/import',
  '/import/phrase',
  '/import/batch'
])
const hostedBookWordsRoute = /^\/books\/[A-Za-z0-9_-]{1,128}\/words$/

function hostedStartUrl() {
  const redirect = route.query.redirect
  const target = typeof redirect === 'string'
    && (hostedReturnRoutes.has(redirect) || hostedBookWordsRoute.test(redirect))
    ? redirect
    : null
  return target === null
    ? '/admin/auth/start'
    : `/admin/auth/start?returnUrl=${encodeURIComponent(target)}`
}

function startHostedLogin() {
  // One click, one navigation: the button stays busy from the first click and
  // the page is about to unload anyway. The browser follows the provider round
  // trip as a top-level navigation to this site's start route; nothing beyond
  // the allowlisted return route is constructed here, and no token protocol
  // field is ever built in the browser.
  if (starting.value) {
    return
  }

  starting.value = true
  window.location.assign(hostedStartUrl())
}

async function handleLogin() {
  if (loading.value) {
    return
  }

  await formRef.value?.validate()
  loading.value = true
  try {
    await login(form.value.username.trim(), form.value.password)
    form.value.password = ''
    await router.replace(getRedirectTarget())
  } catch (error: unknown) {
    const apiError = getApiError(error)
    if (apiError.status === 401) {
      ElMessage.error('用户名或密码错误')
    } else if (apiError.status === 403) {
      ElMessage.error('当前账户没有管理员权限')
    } else {
      ElMessage.error(apiError.message)
    }
  } finally {
    form.value.password = ''
    loading.value = false
  }
}
</script>

<template>
  <div class="auth-page">
    <el-card class="auth-card" shadow="never">
      <template #header>
        <div class="auth-card__heading">
          <div class="auth-card__brand">
            <span class="brand-mark brand-mark--large" aria-hidden="true">L</span>
            <div>
              <h1>Lexarbor</h1>
              <span class="auth-card__product">词汇管理端</span>
            </div>
          </div>
          <p>请使用身份提供方的管理员账户登录</p>
        </div>
      </template>
      <el-alert
        v-if="reasonNotice"
        class="auth-card__reason"
        :type="reasonNotice.type"
        :title="reasonNotice.text"
        :closable="false"
        show-icon
      />
      <p v-if="modeLoading" class="auth-card__pending" role="status">
        正在读取登录方式…
      </p>
      <div v-else-if="modeFailed" class="auth-card__mode-error">
        <el-alert
          type="error"
          title="无法读取登录方式"
          description="未能确认本次部署的登录方式，为避免误提交密码，暂不提供登录动作。"
          :closable="false"
          show-icon
        />
        <el-button class="auth-card__retry" @click="fetchMode">
          重试
        </el-button>
      </div>
      <div v-else-if="mode === 'hosted'" class="auth-card__hosted">
        <p class="auth-card__hint">登录由 SignaCore 托管，本页面不接收密码。</p>
        <el-button
          class="auth-card__action"
          type="primary"
          :loading="starting"
          @click="startHostedLogin"
        >
          使用 SignaCore 登录
        </el-button>
      </div>
      <el-form
        v-else
        ref="formRef"
        :model="form"
        :rules="rules"
        label-position="top"
        @keyup.enter="handleLogin"
      >
        <el-form-item label="用户名" prop="username">
          <el-input
            v-model="form.username"
            autocomplete="username"
            placeholder="请输入用户名"
          />
        </el-form-item>
        <el-form-item label="密码" prop="password">
          <el-input
            v-model="form.password"
            type="password"
            autocomplete="current-password"
            placeholder="请输入密码"
            show-password
          />
        </el-form-item>
        <el-button
          class="auth-card__action"
          type="primary"
          :loading="loading"
          @click="handleLogin"
        >
          登录
        </el-button>
      </el-form>
    </el-card>
  </div>
</template>
