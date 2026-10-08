/**
 * 前端 UI 冒烟测试的独立 Vitest 配置。
 *
 * 刻意与 vite.config.ts 分开：`npm run build` 走的是 `tsc -b && vite build`，
 * 而 tsconfig.node.json 只 include 了 vite.config.ts，所以本文件不参与生产类型检查，
 * 不会因为测试专用类型而影响交付构建。
 */
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    include: ['tests/**/*.test.{ts,tsx}'],
    setupFiles: ['./tests/setup.ts'],
    testTimeout: 15000,
  },
})
