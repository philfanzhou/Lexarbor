<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()
const starting = ref(false)

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
      <div class="auth-card__hosted">
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
    </el-card>
  </div>
</template>
