<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterLink, RouterView } from 'vue-router'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { EditPen, Notebook, Reading, Upload } from '@element-plus/icons-vue'
import { currentUser, isAuthenticated, logout } from '@/services/authState'
import { versionInfo, versionLabel, versionStatus } from '@/services/systemVersion'
import { getApiError } from '@/services/apiError'

const router = useRouter()
const loggingOut = ref(false)

// 教材管理's 查看单词 reaches a single book's word list; 单词管理 is the
// whole-library list. Both belong to the 教材 group.
const navigation = [
  {
    id: 'nav-group-books',
    title: '教材',
    links: [
      { to: '/books', label: '教材管理', icon: Notebook },
      { to: '/vocabulary', label: '单词管理', icon: Reading }
    ]
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

// The build-version detail: a small non-modal disclosure anchored to the
// header button. It opens from the keyboard and from touch, Escape closes it,
// and focus stays on the button that owns it.
const versionOpen = ref(false)
const versionAreaRef = ref<HTMLElement>()
const versionPanelRef = ref<HTMLElement>()
const versionButtonRef = ref<HTMLButtonElement>()

function toggleVersionDetail() {
  if (versionOpen.value) {
    versionOpen.value = false
  } else {
    versionOpen.value = true
    void nextTick(() => versionPanelRef.value?.focus())
  }
}

function closeVersionDetail(returnFocus: boolean) {
  if (!versionOpen.value) {
    return
  }

  versionOpen.value = false
  if (returnFocus) {
    versionButtonRef.value?.focus()
  }
}

function handleDocumentKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    closeVersionDetail(true)
  }
}

function handleDocumentClick(event: MouseEvent) {
  const area = versionAreaRef.value
  if (area && event.target instanceof Node && !area.contains(event.target)) {
    closeVersionDetail(false)
  }
}

onMounted(() => {
  document.addEventListener('keydown', handleDocumentKeydown)
  document.addEventListener('click', handleDocumentClick)
})

onBeforeUnmount(() => {
  document.removeEventListener('keydown', handleDocumentKeydown)
  document.removeEventListener('click', handleDocumentClick)
})

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
      <!-- After the logout button in the DOM so Tab keeps reaching logout
           first; the order below places it back beside the brand. -->
      <div ref="versionAreaRef" class="app-header__version">
        <button
          ref="versionButtonRef"
          type="button"
          class="app-header__version-button"
          :aria-expanded="versionOpen"
          aria-haspopup="dialog"
          @click="toggleVersionDetail"
        >
          <span class="app-header__version-text">{{ versionLabel }}</span>
        </button>
        <div
          v-if="versionOpen"
          ref="versionPanelRef"
          class="version-detail"
          role="dialog"
          aria-label="构建版本详情"
          tabindex="-1"
        >
          <dl v-if="versionStatus === 'ready' && versionInfo" class="version-detail__list">
            <div class="version-detail__row">
              <dt class="version-detail__term">版本</dt>
              <dd class="version-detail__data">
                {{ versionInfo.version === 'unknown' ? '未知' : versionInfo.version }}
              </dd>
            </div>
            <div class="version-detail__row">
              <dt class="version-detail__term">提交</dt>
              <dd class="version-detail__data version-detail__data--mono">
                {{ versionInfo.revision ?? '未知' }}
              </dd>
            </div>
            <div class="version-detail__row">
              <dt class="version-detail__term">渠道</dt>
              <dd class="version-detail__data">{{ versionInfo.channel }}</dd>
            </div>
          </dl>
          <p v-else-if="versionStatus === 'loading'" class="version-detail__note">
            正在获取后端构建信息…
          </p>
          <p v-else class="version-detail__note">
            无法读取后端构建版本，管理功能不受影响。
          </p>
        </div>
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
  height: 56px;
  padding: 0 var(--lx-space-5);
  background: var(--lx-color-bg-surface);
  border-bottom: 1px solid var(--lx-color-border-light);
}
.app-header__brand {
  display: flex;
  align-items: center;
  gap: var(--lx-space-3);
  order: 0;
  min-width: 0;
}
.brand {
  overflow: hidden;
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-lg);
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* After the logout button in the DOM, but laid out beside the brand. */
.app-header__version {
  position: relative;
  order: 1;
  min-width: 0;
  margin-left: var(--lx-space-2);
}
.app-header__version-button {
  display: inline-flex;
  align-items: center;
  max-width: 240px;
  min-width: 0;
  padding: var(--lx-space-1) var(--lx-space-2);
  border: 0;
  border-radius: var(--lx-radius-sm);
  background: transparent;
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-xs);
  line-height: 1.4;
  cursor: pointer;
}
.app-header__version-button:hover,
.app-header__version-button:active {
  color: var(--lx-color-primary);
}
.app-header__version-button:focus-visible {
  outline: 2px solid var(--lx-color-focus);
  outline-offset: 2px;
}
.app-header__version-text {
  overflow: hidden;
  min-width: 0;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.version-detail {
  position: absolute;
  top: calc(100% + var(--lx-space-2));
  left: 0;
  z-index: 20;
  max-width: min(360px, calc(100vw - var(--lx-space-6)));
  padding: var(--lx-space-4);
  background: var(--lx-color-bg-surface);
  border: 1px solid var(--lx-color-border-light);
  border-radius: var(--lx-radius-md);
  box-shadow: var(--lx-shadow-1);
}
.version-detail:focus {
  outline: none;
}
.version-detail__list {
  margin: 0;
}
.version-detail__row {
  display: grid;
  grid-template-columns: minmax(32px, auto) 1fr;
  gap: var(--lx-space-3);
}
.version-detail__row + .version-detail__row {
  margin-top: var(--lx-space-2);
}
.version-detail__term {
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-xs);
  line-height: 1.6;
}
.version-detail__data {
  margin: 0;
  color: var(--lx-color-text-primary);
  font-size: var(--lx-font-size-xs);
  line-height: 1.6;
  word-break: break-all;
}
.version-detail__data--mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}
.version-detail__note {
  margin: 0;
  color: var(--lx-color-text-secondary);
  font-size: var(--lx-font-size-xs);
  line-height: 1.6;
}

.session {
  display: flex;
  align-items: center;
  gap: var(--lx-space-3);
  order: 2;
  min-width: 0;
  margin-left: auto;
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
  .app-header__version-button {
    max-width: 160px;
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
