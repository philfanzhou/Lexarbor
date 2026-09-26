<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { logout } from '@/services/authState'
import { getApiError } from '@/services/apiError'

const router = useRouter()
const loading = ref(false)

async function returnToLogin() {
  if (loading.value) {
    return
  }

  loading.value = true
  try {
    await logout()
  } catch (error: unknown) {
    ElMessage.error(getApiError(error).message)
  } finally {
    loading.value = false
    await router.replace({ name: 'login' })
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
              <p class="auth-card__name">Lexarbor</p>
              <span class="auth-card__product">词汇管理端</span>
            </div>
          </div>
        </div>
      </template>
      <el-result
        class="forbidden-result"
        icon="warning"
        title="无权访问"
        sub-title="Lexarbor 需要身份提供方授予管理员角色。请更换账户后重试。"
      >
        <template #extra>
          <el-button type="primary" :loading="loading" @click="returnToLogin">
            退出并返回登录
          </el-button>
        </template>
      </el-result>
    </el-card>
  </div>
</template>

<style scoped>
.forbidden-result {
  padding: var(--lx-space-2) 0 0;
}
</style>
