import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 3001,
    proxy: {
      '/api/auth': {
        target: 'http://localhost:7072',
        changeOrigin: true,
      },
      '/api/companies': {
        target: 'http://localhost:7072',
        changeOrigin: true,
      },
      '/api/warehouses': {
        target: 'http://localhost:7072',
        changeOrigin: true,
      },
      '/api': {
        target: 'http://localhost:7071',
        changeOrigin: true,
      },
    },
  },
})
