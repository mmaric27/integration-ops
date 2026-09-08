import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    host: 'localhost',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': {
        target: 'https://localhost:7004',
        secure: false,
      },
    },
  },
  test: {
    environment: 'jsdom',
    maxWorkers: 1,
    pool: 'threads',
    setupFiles: './src/setupTests.ts',
  },
})
