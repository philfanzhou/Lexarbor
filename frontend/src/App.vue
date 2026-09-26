<script setup lang="ts">
import { ref } from 'vue'
import { RouterLink, RouterView } from 'vue-router'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { EditPen, Notebook, Upload } from '@element-plus/icons-vue'
import { currentUser, isAuthenticated, logout } from '@/services/authState'
import { getApiError } from '@/services/apiError'

const router = useRouter()
const loggingOut = ref(false)

// A later book word list page belongs to the 教材 group.
const navigation = [
  {
    id: 'nav-group-books',
    title: '教材',
    links: [{ to: '/books', label: '教材管理', icon: Notebook }]
  },
  {
    id: 'nav-group-import',
    title: '词汇导入',
    links: [
      { to: '/import', label: '单条导入', icon: EditPen },
      { to: '/import/batch', label: '批量导入', icon: Upload }
    ]
  }
]

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
  <div v-if="isAuthenticated" class="app-shell">
    <header class="app-header">
      <div class="app-header__brand">
        <span class="brand-mark" aria-hidden="true">L</span>
        <span class="brand">Lexarbor</span>
      </div>
      <div class="session">
        <span class="session__user">{{ currentUser?.username }}</span>
        <el-button link type="primary" :loading="loggingOut" @click="handleLogout">
          退出登录
        </el-button>
      </div>
    </header>
    <div class="app-body">
      <nav class="app-nav" aria-label="主导航">
        <div
          v-for="group in navigation"
          :key="group.id"
          class="app-nav__group"
          role="group"
          :aria-labelledby="group.id"
        >
          <span :id="group.id" class="app-nav__group-title">{{ group.title }}</span>
          <ul class="app-nav__list">
            <li v-for="link in group.links" :key="link.to">
              <RouterLink
                :to="link.to"
                class="app-nav__link"
                exact-active-class="is-active"
                :aria-label="link.label"
                :title="link.label"
              >
                <el-icon class="app-nav__icon" aria-hidden="true">
                  <component :is="link.icon" />
                </el-icon>
                <span class="app-nav__text">{{ link.label }}</span>
              </RouterLink>
            </li>
          </ul>
        </div>
      </nav>
      <main class="app-main">
        <div class="app-content">
          <RouterView />
        </div>
      </main>
    </div>
  </div>
  <main v-else>
    <RouterView />
  </main>
</template>

<style scoped>
.app-shell {
  min-height: 100vh;
  background: var(--lx-color-bg-page);
}

.app-header {
  position: sticky;
  top: 0;
  z-index: 10;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--lx-space-5);
  height: 56px;
  padding: 0 var(--lx-space-5);
  background: var(--lx-color-bg-surface);
  border-bottom: 1px solid var(--lx-color-border-light);
}
.app-header__brand {
  display: flex;
  align-items: center;
  gap: var(--lx-space-3);
}
.brand {
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-lg);
  font-weight: 600;
}
.session {
  display: flex;
  align-items: center;
  gap: var(--lx-space-3);
  min-width: 0;
  color: var(--lx-color-text-regular);
  font-size: var(--lx-font-size-sm);
}
.session__user {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.app-body {
  display: flex;
  align-items: flex-start;
}

.app-nav {
  position: sticky;
  top: 56px;
  flex: none;
  width: 220px;
  height: calc(100vh - 56px);
  padding: var(--lx-space-4) var(--lx-space-3);
  overflow-y: auto;
  background: var(--lx-color-bg-surface);
  border-right: 1px solid var(--lx-color-border-light);
}
.app-nav__group + .app-nav__group {
  margin-top: var(--lx-space-5);
}
.app-nav__group-title {
  display: block;
  padding: 0 var(--lx-space-3);
  margin-bottom: var(--lx-space-2);
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-xs);
  font-weight: 600;
}
.app-nav__list {
  display: flex;
  flex-direction: column;
  gap: var(--lx-space-1);
  margin: 0;
  padding: 0;
  list-style: none;
}
.app-nav__link {
  display: flex;
  align-items: center;
  gap: var(--lx-space-3);
  height: 40px;
  padding: 0 var(--lx-space-3);
  border-radius: var(--lx-radius-sm);
  color: var(--lx-color-text-regular);
  font-size: var(--lx-font-size-sm);
  text-decoration: none;
}
.app-nav__link:hover,
.app-nav__link:active,
.app-nav__link.is-active {
  background: var(--lx-color-primary-soft);
  color: var(--lx-color-primary);
}
.app-nav__link.is-active {
  font-weight: 600;
}
.app-nav__link:focus-visible {
  outline: 2px solid var(--lx-color-focus);
  outline-offset: 2px;
}
.app-nav__icon {
  flex: none;
  font-size: var(--lx-font-size-md);
}

.app-main {
  flex: 1;
  min-width: 0;
}
.app-content {
  max-width: 1200px;
  margin: 0 auto;
  padding: var(--lx-space-5);
}

@media (max-width: 1023px) {
  .app-header {
    padding: 0 var(--lx-space-4);
  }
  .app-nav {
    width: 64px;
    padding: var(--lx-space-4) var(--lx-space-3);
  }
  .app-nav__group + .app-nav__group {
    margin-top: var(--lx-space-4);
    padding-top: var(--lx-space-4);
    border-top: 1px solid var(--lx-color-border-light);
  }
  .app-nav__group-title,
  .app-nav__text {
    position: absolute;
    width: 1px;
    height: 1px;
    margin: -1px;
    padding: 0;
    overflow: hidden;
    clip: rect(0 0 0 0);
    white-space: nowrap;
    border: 0;
  }
  .app-nav__link {
    justify-content: center;
    padding: 0;
  }
  .app-nav__icon {
    font-size: var(--lx-font-size-lg);
  }
  .app-content {
    padding: var(--lx-space-4);
  }
}
</style>
