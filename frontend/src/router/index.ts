import { createRouter, createWebHashHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'
import { getApiError } from '@/services/apiError'
import { isAuthenticated, restoreSession } from '@/services/authState'
import BatchImportView from '@/views/BatchImportView.vue'
import BookWordsView from '@/views/BookWordsView.vue'
import BooksView from '@/views/BooksView.vue'
import ForbiddenView from '@/views/ForbiddenView.vue'
import ImportView from '@/views/ImportView.vue'
import PhraseImportView from '@/views/PhraseImportView.vue'
import LoginView from '@/views/LoginView.vue'
import PhrasePositionsView from '@/views/PhrasePositionsView.vue'
import VocabularyView from '@/views/VocabularyView.vue'

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/books' },
  { path: '/login', name: 'login', component: LoginView, meta: { public: true } },
  { path: '/forbidden', name: 'forbidden', component: ForbiddenView, meta: { public: true } },
  { path: '/books', name: 'books', component: BooksView },
  { path: '/books/:bookId/words', name: 'book-words', component: BookWordsView },
  { path: '/vocabulary', name: 'vocabulary', component: VocabularyView },
  { path: '/phrases', name: 'phrase-positions', component: PhrasePositionsView },
  { path: '/import', name: 'import', component: ImportView },
  { path: '/import/phrase', name: 'phrase-import', component: PhraseImportView },
  { path: '/import/batch', name: 'batch-import', component: BatchImportView, props: { mode: 'mixed' } },
  { path: '/import/batch/words', name: 'batch-import-words', component: BatchImportView, props: { mode: 'word' } },
  { path: '/import/batch/phrases', name: 'batch-import-phrases', component: BatchImportView, props: { mode: 'phrase' } }
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
