import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', '')
  const proxy = {
    '/api': { target: env.API_PROXY_TARGET || 'http://localhost:62680', changeOrigin: true },
  }
  return { plugins: [vue()], server: { proxy }, preview: { proxy } }
})
