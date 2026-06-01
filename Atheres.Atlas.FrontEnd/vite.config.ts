import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 3000,
    proxy: {
      '/api/auth': {
        target: 'http://localhost:7072',
        changeOrigin: true,
      },
      '/api/users': {
        target: 'http://localhost:7072',
        changeOrigin: true,
      },
      '/api/companies': {
        target: 'http://localhost:7072',
        changeOrigin: true,
      },
      // Hubs and Warehouses moved to the main Function App (:7071); they fall
      // through the '/api' rule below. Only auth/users/companies stay on :7072.
      '/api': {
        target: 'http://localhost:7071',
        changeOrigin: true,
      },
    },
  },
})
