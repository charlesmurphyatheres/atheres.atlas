import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Production builds serve under /demo/ on atlasdeliver.com (AFD route-demo
// pattern is /demo/*, which forwards the path unchanged to the demo storage
// origin). Setting base: '/demo/' makes Vite emit /demo/assets/... references
// so the bundle resolves correctly when served from that prefix. The matching
// upload step in upgrade-azure.ps1 places dist/ inside $web/demo/ so storage
// actually has files at those paths.
// Dev (command='serve') keeps base: '/' so localhost:3001 still works.
export default defineConfig(({ command }) => ({
  plugins: [react()],
  base: command === 'build' ? '/demo/' : '/',
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
}))
