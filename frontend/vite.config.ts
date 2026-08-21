import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import AutoImport from 'unplugin-auto-import/vite'
import Components from 'unplugin-vue-components/vite'
import { ElementPlusResolver } from 'unplugin-vue-components/resolvers'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  plugins: [
    vue(),
    AutoImport({ resolvers: [ElementPlusResolver({ importStyle: false })] }),
    Components({ resolvers: [ElementPlusResolver({ importStyle: false })] })
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url))
    }
  },
  server: {
    host: '0.0.0.0',
    port: 5175,
    proxy: {
      '/admin': {
        target: 'http://localhost:5008',
        changeOrigin: true
      },
      // The administration app reads the public catalogue too: the import
      // page's book picker wants every enabled book, which is what
      // /api/vocabulary-books/all answers. Same origin once built, so this
      // proxy is what makes the dev server match production.
      '/api': {
        target: 'http://localhost:5008',
        changeOrigin: true
      }
    }
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    chunkSizeWarningLimit: 600,
    rolldownOptions: {
      onLog(_level, log) {
        if (log.code === 'INVALID_ANNOTATION') return
      }
    }
  }
})
