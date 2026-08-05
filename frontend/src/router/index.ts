import { createRouter, createWebHashHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'
import { getApiError } from '@/services/apiError'
import { isAuthenticated, restoreSession } from '@/services/authState'
import BooksView from '@/views/BooksView.vue'
import ForbiddenView from '@/views/ForbiddenView.vue'
import ImportView from '@/views/ImportView.vue'
import LoginView from '@/views/LoginView.vue'

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/books' },
  { path: '/login', name: 'login', component: LoginView, meta: { public: true } },
  { path: '/forbidden', name: 'forbidden', component: ForbiddenView, meta: { public: true } },
  { path: '/books', name: 'books', component: BooksView },
  { path: '/import', name: 'import', component: ImportView }
]

const router = createRouter({
  history: createWebHashHistory(),
  routes
})

let sessionRestoreAttempted = false

router.beforeEach(async (to) => {
  if (to.meta.public) {
    return true
  }

  if (!sessionRestoreAttempted) {
    sessionRestoreAttempted = true
    try {
      await restoreSession()
    } catch (error: unknown) {
      if (getApiError(error).status === 403) {
        return { name: 'forbidden' }
      }

      return {
        name: 'login',
        query: { redirect: to.fullPath }
      }
    }
  }

  if (!isAuthenticated.value) {
    return {
      name: 'login',
      query: { redirect: to.fullPath }
    }
  }

  return true
})

export default router
