import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// 采集后端（ASP.NET Core，见 backend/src/UavPlatform.Api）。
// 可用环境变量 UAVPLATFORM_BACKEND 覆盖，默认 http://localhost:5080。
const BACKEND = process.env.UAVPLATFORM_BACKEND ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: BACKEND, changeOrigin: true },
      '/hubs': { target: BACKEND, changeOrigin: true, ws: true },
    },
  },
  build: {
    outDir: '../backend/src/UavPlatform.Api/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 2000,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes('node_modules')) {
            if (id.includes('three') || id.includes('@react-three')) return 'three'
            if (
              id.includes('react') ||
              id.includes('zustand') ||
              id.includes('@microsoft/signalr')
            ) {
              return 'vendor'
            }
          }
        },
      },
    },
  },
})
