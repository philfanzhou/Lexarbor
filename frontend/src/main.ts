import { createApp } from 'vue'
import App from './App.vue'
import router from './router'
import { setAuthFailureHandlers } from './services/api'
import { clearSession } from './services/authState'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import './styles/app.scss'

setAuthFailureHandlers(
  () => {
    clearSession()
    const routeName = router.currentRoute.value.name
    if (routeName && routeName !== 'login') {
      void router.replace({
        name: 'login',
        query: { redirect: router.currentRoute.value.fullPath }
      })
    }
  },
  () => {
    clearSession()
    const routeName = router.currentRoute.value.name
    if (routeName && routeName !== 'forbidden') {
      void router.replace({ name: 'forbidden' })
    }
  }
)

const app = createApp(App)
app.use(router)
app.use(ElementPlus)
app.mount('#app')
