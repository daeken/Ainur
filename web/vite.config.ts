import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/Ainur.Server/wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': { target: 'http://127.0.0.1:5180', changeOrigin: false } } },
})
