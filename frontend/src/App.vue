<script setup lang="ts">
import { ref } from 'vue'
import { RouterLink, RouterView } from 'vue-router'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { currentUser, isAuthenticated, logout } from '@/services/authState'
import { getApiError } from '@/services/apiError'

const router = useRouter()
const loggingOut = ref(false)

async function handleLogout() {
  if (loggingOut.value) {
    return
  }

  loggingOut.value = true
  try {
    await logout()
  } catch (error: unknown) {
    ElMessage.error(getApiError(error).message)
  } finally {
    loggingOut.value = false
    await router.replace({ name: 'login' })
  }
}
</script>

<template>
  <div class="vocabulary-admin">
    <header v-if="isAuthenticated" class="app-header">
      <div class="brand">Lexarbor</div>
      <nav class="nav">
        <RouterLink to="/books">教材管理</RouterLink>
        <RouterLink to="/import">单词导入</RouterLink>
      </nav>
      <div class="session">
        <span>{{ currentUser?.username }}</span>
        <el-button link type="primary" :loading="loggingOut" @click="handleLogout">
          退出登录
        </el-button>
      </div>
    </header>
    <main :class="{ 'app-main': isAuthenticated }">
      <RouterView />
    </main>
  </div>
</template>

<style scoped>
.vocabulary-admin {
  min-height: 100vh;
  background: #f5f7fa;
}
.app-header {
  display: flex;
  align-items: center;
  gap: 32px;
  height: 56px;
  padding: 0 24px;
  background: #fff;
  border-bottom: 1px solid #ebeef5;
}
.brand {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}
.nav {
  display: flex;
  gap: 24px;
}
.nav a {
  color: #606266;
  text-decoration: none;
  font-size: 14px;
}
.nav a.router-link-active {
  color: #409eff;
  font-weight: 600;
}
.session {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-left: auto;
  color: #606266;
  font-size: 14px;
}
.app-main {
  max-width: 1200px;
  margin: 0 auto;
}
</style>
