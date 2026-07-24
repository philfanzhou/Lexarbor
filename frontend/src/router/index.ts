import { createRouter, createWebHashHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'
import BooksView from '@/views/BooksView.vue'
import ImportView from '@/views/ImportView.vue'

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/books' },
  { path: '/books', name: 'books', component: BooksView },
  { path: '/import', name: 'import', component: ImportView }
]

const router = createRouter({
  history: createWebHashHistory(),
  routes
})

export default router
